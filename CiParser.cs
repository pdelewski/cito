// CiParser.cs - Ci parser
//
// Copyright (C) 2011-2022  Piotr Fusik
//
// This file is part of CiTo, see https://github.com/pfusik/cito
//
// CiTo is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// CiTo is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with CiTo.  If not, see http://www.gnu.org/licenses/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Foxoft.Ci
{

public class CiParser : CiParserBase
{
	public CiProgram Program;

	string ParseId()
	{
		string id = this.StringValue;
		Expect(CiToken.Id);
		return id;
	}

	static void AppendUtf16(StringBuilder sb, int c)
	{
		if (c >= 0x10000) {
			sb.Append((char) (0xd800 + (c - 0x10000 >> 10 & 0x3ff)));
			c = 0xdc00 + (c & 0x3ff);
		}
		sb.Append((char) c);
	}

	protected override string DocParseText()
	{
		StringBuilder sb = new StringBuilder();
		while (DocSee(CiDocToken.Char)) {
			AppendUtf16(sb, this.DocCurrentChar);
			DocNextToken();
		}
		if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
			sb.Length--;
		return sb.ToString();
	}

	CiCodeDoc ParseDoc() => See(CiToken.DocComment) ? ParseCodeDoc() : null;

	CiInterpolatedString ParseInterpolatedString()
	{
		CiInterpolatedString result = new CiInterpolatedString { Line = this.Line, Type = CiSystem.StringStorageType };
		do {
			string prefix = this.StringValue.Replace("{{", "{");
			NextToken();
			CiExpr arg = ParseExpr();
			CiExpr width = null;
			char format = ' ';
			int precision = -1;
			if (Eat(CiToken.Comma))
				width = ParseExpr();
			if (See(CiToken.Colon)) {
				format = (char) ReadChar();
				if ("DdEeFfGgXx".IndexOf(format) < 0)
					ReportError("Invalid format specifier");

				if (SeeDigit()) {
					precision = ReadChar() - '0';
					if (SeeDigit())
						precision = precision * 10 + ReadChar() - '0';
				}
				NextToken();
			}
			result.AddPart(prefix, arg, width, format, precision);
			Check(CiToken.RightBrace);
		} while (ReadString(true) == CiToken.InterpolatedString);
		result.Suffix = this.StringValue.Replace("{{", "{");
		NextToken();
		return result;
	}

	protected override CiExpr ParsePrimaryExpr()
	{
		CiExpr result;
		switch (this.CurrentToken) {
		case CiToken.Increment:
		case CiToken.Decrement:
			CheckXcrementParent();
			goto case CiToken.Minus;
		case CiToken.Minus:
		case CiToken.Tilde:
		case CiToken.ExclamationMark:
			return new CiPrefixExpr { Line = this.Line, Op = NextToken(), Inner = ParsePrimaryExpr() };
		case CiToken.New:
			CiPrefixExpr newResult = new CiPrefixExpr { Line = this.Line, Op = NextToken() };
			result = ParseType();
			if (Eat(CiToken.LeftBrace))
				result = new CiBinaryExpr { Line = this.Line, Left = result, Op = CiToken.LeftBrace, Right = ParseObjectLiteral() };
			newResult.Inner = result;
			return newResult;
		case CiToken.LiteralLong:
			result = CiSystem.NewLiteralLong(this.LongValue, this.Line);
			NextToken();
			break;
		case CiToken.LiteralDouble:
			if (!double.TryParse(GetLexeme().Replace("_", ""),
				NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent | NumberStyles.AllowLeadingSign,
				CultureInfo.InvariantCulture, out double d))
				ReportError("Invalid floating-point number");
			result = new CiLiteralDouble { Line = this.Line, Type = CiSystem.DoubleType, Value = d };
			NextToken();
			break;
		case CiToken.LiteralChar:
			result = CiLiteralChar.New((int) this.LongValue, this.Line);
			NextToken();
			break;
		case CiToken.LiteralString:
			result = CiSystem.NewLiteralString(this.StringValue, this.Line);
			NextToken();
			break;
		case CiToken.False:
			result = new CiLiteralFalse { Line = this.Line, Type = CiSystem.BoolType };
			NextToken();
			break;
		case CiToken.True:
			result = new CiLiteralTrue { Line = this.Line, Type = CiSystem.BoolType };
			NextToken();
			break;
		case CiToken.Null:
			result = new CiLiteralNull { Line = this.Line, Type = CiSystem.NullType };
			NextToken();
			break;
		case CiToken.InterpolatedString:
			result = ParseInterpolatedString();
			break;
		case CiToken.LeftParenthesis:
			result = ParseParenthesized();
			break;
		case CiToken.Id:
			result = ParseSymbolReference(null);
			if (Eat(CiToken.FatArrow)) {
				CiLambdaExpr lambda = new CiLambdaExpr { Line = result.Line };
				lambda.Add(CiVar.New(null, ((CiSymbolReference) result).Name));
				lambda.Body = ParseExpr();
				return lambda;
			}
			break;
		case CiToken.Resource:
			NextToken();
			if (Eat(CiToken.Less)
			 && ParseId() == "byte"
			 && Eat(CiToken.LeftBracket)
			 && Eat(CiToken.RightBracket)
			 && Eat(CiToken.Greater))
				result = new CiPrefixExpr { Line = this.Line, Op = CiToken.Resource, Inner = ParseParenthesized() };
			else {
				ReportError("Expected 'resource<byte[]>'");
				result = null;
			}
			break;
		default:
			ReportError("Invalid expression");
			result = null;
			break;
		}
		for (;;) {
			switch (this.CurrentToken) {
			case CiToken.Dot:
				NextToken();
				result = ParseSymbolReference(result);
				break;
			case CiToken.LeftParenthesis:
				CiSymbolReference symbol = result as CiSymbolReference;
				if (symbol == null)
					ReportError("Expected a method");
				NextToken();
				CiCallExpr call = new CiCallExpr { Line = this.Line, Method = symbol };
				ParseCollection(call.Arguments, CiToken.RightParenthesis);
				result = call;
				break;
			case CiToken.LeftBracket:
				result = new CiBinaryExpr { Line = this.Line, Left = result, Op = NextToken(), Right = See(CiToken.RightBracket) ? null : ParseExpr() };
				Expect(CiToken.RightBracket);
				break;
			case CiToken.Increment:
			case CiToken.Decrement:
				CheckXcrementParent();
				goto case CiToken.ExclamationMark;
			case CiToken.ExclamationMark:
			case CiToken.Hash:
				result = new CiPostfixExpr { Line = this.Line, Inner = result, Op = NextToken() };
				break;
			default:
				return result;
			}
		}
	}

	CiExpr ParseAssign(bool allowVar)
	{
		CiExpr left = allowVar ? ParseType() : ParseExpr();
		switch (this.CurrentToken) {
		case CiToken.Assign:
		case CiToken.AddAssign:
		case CiToken.SubAssign:
		case CiToken.MulAssign:
		case CiToken.DivAssign:
		case CiToken.ModAssign:
		case CiToken.AndAssign:
		case CiToken.OrAssign:
		case CiToken.XorAssign:
		case CiToken.ShiftLeftAssign:
		case CiToken.ShiftRightAssign:
			return new CiBinaryExpr { Line = this.Line, Left = left, Op = NextToken(), Right = ParseAssign(false) };
		case CiToken.Id:
			if (allowVar)
				return ParseVar(left);
			return left;
		default:
			return left;
		}
	}

	CiFor ParseFor()
	{
		CiFor result = new CiFor { Line = this.Line };
		Expect(CiToken.For);
		Expect(CiToken.LeftParenthesis);
		if (!See(CiToken.Semicolon))
			result.Init = ParseAssign(true);
		Expect(CiToken.Semicolon);
		if (!See(CiToken.Semicolon))
			result.Cond = ParseExpr();
		Expect(CiToken.Semicolon);
		if (!See(CiToken.RightParenthesis))
			result.Advance = ParseAssign(false);
		Expect(CiToken.RightParenthesis);
		ParseLoopBody(result);
		return result;
	}

	CiNative ParseNative()
	{
		int line = this.Line;
		Expect(CiToken.Native);
		int offset = this.CharOffset;
		Expect(CiToken.LeftBrace);
		int nesting = 1;
		for (;;) {
			if (See(CiToken.EndOfFile)) {
				Expect(CiToken.RightBrace);
				return null;
			}
			if (See(CiToken.LeftBrace))
				nesting++;
			else if (See(CiToken.RightBrace) && --nesting == 0)
				break;
			NextToken();
		}
		Trace.Assert(this.Input[this.CharOffset - 1] == '}');
		string content = Encoding.UTF8.GetString(this.Input, offset, this.CharOffset - 1 - offset);
		NextToken();
		return new CiNative { Line = line, Content = content };
	}

	CiSwitch ParseSwitch()
	{
		CiSwitch result = new CiSwitch { Line = this.Line };
		Expect(CiToken.Switch);
		result.Value = ParseParenthesized();
		Expect(CiToken.LeftBrace);

		CiCondCompletionStatement outerLoopOrSwitch = this.CurrentLoopOrSwitch;
		this.CurrentLoopOrSwitch = result;
		while (Eat(CiToken.Case)) {
			CiCase kase = new CiCase();
			do {
				CiExpr expr = ParseExpr();
				if (See(CiToken.Id))
					expr = ParseVar(expr);
				kase.Values.Add(expr);
				Expect(CiToken.Colon);
			} while (Eat(CiToken.Case));
			if (See(CiToken.Default)) {
				ReportError("Please remove 'case' before 'default'");
				break;
			}

			while (!See(CiToken.EndOfFile)) {
				kase.Body.Add(ParseStatement());
				switch (this.CurrentToken) {
				case CiToken.Case:
				case CiToken.Default:
				case CiToken.RightBrace:
					break;
				default:
					continue;
				}
				break;
			}
			result.Cases.Add(kase);
		}
		if (result.Cases.Count == 0)
			ReportError("Switch with no cases");

		if (Eat(CiToken.Default)) {
			Expect(CiToken.Colon);
			do {
				if (See(CiToken.EndOfFile))
					break;
				result.DefaultBody.Add(ParseStatement());
			} while (!See(CiToken.RightBrace));
		}

		Expect(CiToken.RightBrace);
		this.CurrentLoopOrSwitch = outerLoopOrSwitch;
		return result;
	}

	protected override CiStatement ParseStatement()
	{
		switch (this.CurrentToken) {
		case CiToken.LeftBrace:
			return ParseBlock();
		case CiToken.Assert:
			return ParseAssert();
		case CiToken.Break:
			return ParseBreak();
		case CiToken.Const:
			return ParseConst();
		case CiToken.Continue:
			return ParseContinue();
		case CiToken.Do:
			return ParseDoWhile();
		case CiToken.For:
			return ParseFor();
		case CiToken.Foreach:
			return ParseForeach();
		case CiToken.If:
			return ParseIf();
		case CiToken.Lock_:
			return ParseLock();
		case CiToken.Native:
			return ParseNative();
		case CiToken.Return:
			return ParseReturn();
		case CiToken.Switch:
			return ParseSwitch();
		case CiToken.Throw:
			return ParseThrow();
		case CiToken.While:
			return ParseWhile();
		default:
			CiExpr expr = ParseAssign(true);
			Expect(CiToken.Semicolon);
			return expr;
		}
	}

	void ParseMethod(CiMethod method)
	{
		method.IsMutator = Eat(CiToken.ExclamationMark);
		Expect(CiToken.LeftParenthesis);
		if (!See(CiToken.RightParenthesis)) {
			do {
				CiCodeDoc doc = ParseDoc();
				CiVar param = ParseVar(ParseType());
				param.Documentation = doc;
				AddSymbol(method.Parameters, param);
			} while (Eat(CiToken.Comma));
		}
		Expect(CiToken.RightParenthesis);
		method.Throws = Eat(CiToken.Throws);
		if (method.CallType == CiCallType.Abstract)
			Expect(CiToken.Semicolon);
		else if (See(CiToken.FatArrow))
			method.Body = ParseReturn();
		else if (Check(CiToken.LeftBrace))
			method.Body = ParseBlock();
	}

	static readonly string[] CallTypeStrings = {
		"static",
		"normal",
		"abstract",
		"virtual",
		"override",
		"sealed"
	};

	static string ToString(CiCallType callType) => CallTypeStrings[(int) callType];

	public CiClass ParseClass(CiCallType callType)
	{
		Expect(CiToken.Class);
		CiClass klass = new CiClass { Parent = this.Program, Filename = this.Filename, Line = this.Line, CallType = callType, Name = ParseId() };
		if (Eat(CiToken.Colon))
			klass.BaseClassName = ParseId();
		Expect(CiToken.LeftBrace);

		while (!See(CiToken.RightBrace) && !See(CiToken.EndOfFile)) {
			CiCodeDoc doc = ParseDoc();

			CiVisibility visibility;
			switch (this.CurrentToken) {
			case CiToken.Internal:
				visibility = CiVisibility.Internal;
				NextToken();
				break;
			case CiToken.Protected:
				visibility = CiVisibility.Protected;
				NextToken();
				break;
			case CiToken.Public:
				visibility = CiVisibility.Public;
				NextToken();
				break;
			case CiToken.Semicolon:
				ReportError("Semicolon in class definition");
				NextToken();
				continue;

			default:
				visibility = CiVisibility.Private;
				break;
			}

			if (See(CiToken.Const)) {
				// const
				CiConst konst = ParseConst();
				konst.Documentation = doc;
				konst.Visibility = visibility;
				AddSymbol(klass, konst);
				continue;
			}

			callType = ParseCallType();
			CiExpr type = Eat(CiToken.Void) ? CiSystem.VoidType : ParseType();
			if (See(CiToken.LeftBrace) && type is CiCallExpr call) {
				// constructor
				if (call.Method.Name != klass.Name)
					ReportError("Method with no return type");
				else {
					if (klass.CallType == CiCallType.Static)
						ReportError("Constructor in a static class");
					if (callType != CiCallType.Normal)
						ReportError($"Constructor cannot be {ToString(callType)}");
					if (call.Arguments.Count != 0)
						ReportError("Constructor parameters not supported");
					if (klass.Constructor != null)
						ReportError($"Duplicate constructor, already defined in line {klass.Constructor.Line}");
				}
				if (visibility == CiVisibility.Private)
					visibility = CiVisibility.Internal; // TODO
				klass.Constructor = new CiMethodBase { Line = call.Line, Documentation = doc, Visibility = visibility, Parent = klass, Type = CiSystem.VoidType, Name = klass.Name, Body = ParseBlock() };
				continue;
			}

			int line = this.Line;
			string name = ParseId();
			if (See(CiToken.LeftParenthesis) || See(CiToken.ExclamationMark)) {
				// method

				// \ class | static | normal | abstract | sealed
				// method \|        |        |          |
				// --------+--------+--------+----------+-------
				// static  |   +    |   +    |    +     |   +
				// normal  |   -    |   +    |    +     |   +
				// abstract|   -    |   -    |    +     |   -
				// virtual |   -    |   +    |    +     |   -
				// override|   -    |   +    |    +     |   +
				// sealed  |   -    |   +    |    +     |   +
				if (callType == CiCallType.Static || klass.CallType == CiCallType.Abstract) {
					// ok
				}
				else if (klass.CallType == CiCallType.Static)
					ReportError("Only static methods allowed in a static class");
				else if (callType == CiCallType.Abstract)
					ReportError("Abstract methods allowed only in an abstract class");
				else if (klass.CallType == CiCallType.Sealed && callType == CiCallType.Virtual)
					ReportError("Virtual methods disallowed in a sealed class");
				if (visibility == CiVisibility.Private && callType != CiCallType.Static && callType != CiCallType.Normal)
					ReportError($"{callType} method cannot be private");

				CiMethod method = new CiMethod { Line = line, Documentation = doc, Visibility = visibility, CallType = callType, TypeExpr = type, Name = name };
				method.Parameters.Parent = klass;
				ParseMethod(method);
				AddSymbol(klass, method);
				continue;
			}

			// field
			if (visibility == CiVisibility.Public)
				ReportError("Field cannot be public");
			if (callType != CiCallType.Normal)
				ReportError($"Field cannot be {ToString(callType)}");
			if (type == CiSystem.VoidType)
				ReportError("Field cannot be void");
			CiField field = new CiField { Line = line, Documentation = doc, Visibility = visibility, TypeExpr = type, Name = name, Value = ParseInitializer() };
			Expect(CiToken.Semicolon);
			AddSymbol(klass, field);
		}
		Expect(CiToken.RightBrace);
		return klass;
	}

	public CiEnum ParseEnum()
	{
		Expect(CiToken.Enum);
		bool flags = Eat(CiToken.Asterisk);
		CiEnum enu = flags ? new CiEnumFlags() : new CiEnum();
		enu.Parent = this.Program;
		enu.Filename = this.Filename;
		enu.Line = this.Line;
		enu.Name = ParseId();
		Expect(CiToken.LeftBrace);
		do {
			CiConst konst = new CiConst { Visibility = CiVisibility.Public, Documentation = ParseDoc(), Line = this.Line, Name = ParseId(), Type = enu };
			if (Eat(CiToken.Assign))
				konst.Value = ParseExpr();
			else if (flags)
				ReportError("enum* symbol must be assigned a value");
			AddSymbol(enu, konst);
		} while (Eat(CiToken.Comma));
		Expect(CiToken.RightBrace);
		return enu;
	}

	public void Parse(string filename, byte[] input)
	{
		Open(filename, input, input.Length);
		while (!See(CiToken.EndOfFile)) {
			CiCodeDoc doc = ParseDoc();
			CiContainerType type;
			bool isPublic = Eat(CiToken.Public);
			switch (this.CurrentToken) {
			// class
			case CiToken.Class:
				type = ParseClass(CiCallType.Normal);
				break;
			case CiToken.Static:
			case CiToken.Abstract:
			case CiToken.Sealed:
				type = ParseClass(ParseCallType());
				break;

			// enum
			case CiToken.Enum:
				type = ParseEnum();
				break;

			// native
			case CiToken.Native:
				this.Program.TopLevelNatives.Add(ParseNative().Content);
				continue;

			default:
				ReportError("Expected class or enum");
				NextToken();
				continue;
			}
			type.Documentation = doc;
			type.IsPublic = isPublic;
			AddSymbol(this.Program, type);
		}
	}
}

}
