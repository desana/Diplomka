﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;

using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;

using static KSharpParser.KSharpGrammarParser;

namespace KSharpParser
{
    /// <summary>
    /// Provides evaluation of the macro expression. Implements <see cref="KSharpGrammarBaseVisitor{Result}"/>.
    /// </summary>
    internal class KSharpVisitor : KSharpGrammarBaseVisitor<object>
    {
        private readonly INodeEvaluator evaluator;

        private CancellationToken? cancellationToken;

        private CancellationToken Token => cancellationToken.Value;

        private IDictionary<string, object> localVariables = new Dictionary<string, object>();

        private bool breakLoop = false;

        private bool continueLoop = false;

        private bool returnFromLoop = false;
        
        private static readonly Type[] mNumericTypes = new Type[] {
            typeof(int),
            typeof(double),
            typeof(long),
            typeof(short),
            typeof(float),
            typeof(Int16),
            typeof(Int32),
            typeof(Int64),
            typeof(uint),
            typeof(UInt16),
            typeof(UInt32),
            typeof(UInt64),
            typeof(sbyte),
            typeof(Single),
            typeof(decimal)
        };


        /// <summary>
        /// Initializes a new instance of the <see cref="KSharpVisitor"/> class.
        /// </summary>
        /// <param name="evaluator">Instance of the evaluator.</param>
        public KSharpVisitor(INodeEvaluator evaluator)
        {
            this.evaluator = evaluator;
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="KSharpVisitor"/> class for lambda expression body.
        /// </summary>
        /// <param name="evaluator">Instance of the evaluator.</param>
        /// <param name="globalVariables">Variables from the visitor above.</param>
        /// <param name="parameterNames">Lambda parameter names.</param>
        /// <param name="parameterValues">Lambda parameter values.</param>
        private KSharpVisitor(INodeEvaluator evaluator, IDictionary<string, object> globalVariables, string[] parameterNames, object[] parameterValues, CancellationToken cancellationToken) : this(evaluator)
        {
            InitGlobalVariables(globalVariables);
            if (parameterNames != null)
            {
                InitLocalVariables(parameterNames, parameterValues);
            }

            this.cancellationToken = cancellationToken;
        }


        /// <summary>
        /// Evaluates cumulated expressions like "1 * 1 * 1".
        /// </summary>
        /// <typeparam name="T">Input type of <paramref name="evaluate"/> method.</typeparam>
        /// <typeparam name="TT">Output type of <paramref name="evaluate"/> method.</typeparam>
        /// <param name="context">Context of the parser rule from which the method was called.</param>
        /// <param name="evaluate">Operation or method to be applied on operands.</param>
        /// <param name="convert">Method which converts operands to correct type for <paramref name="evaluate"/> method.</param>
        /// <returns>Expression result.</returns>
        private object EvaluateAllAlternatives<T, TT>(ParserRuleContext context, Func<TT, TT, T> evaluate, Func<object, TT> convert)
        {
            var leftOperand = Visit(context.GetChild(0));

            int numberOfOperands = context.children.Count;
            for (var i = 2; i < numberOfOperands; i += 2)
            {
                var rightOperandContext = context.GetChild(i);
                if (rightOperandContext == null)
                {
                    return leftOperand;
                }

                var rightOperand = Visit(rightOperandContext);

                if (convert != null)
                { 
                    leftOperand = evaluate(convert(leftOperand), convert(rightOperand));
                }
                else 
                {
                    leftOperand = evaluate((TT)leftOperand, (TT)rightOperand);
                }
            }

            return leftOperand;
        }


        /// <summary>
        /// Invokes and evaluates lambda expression given by <paramref name="context"/>.
        /// </summary>
        /// <param name="context">Context of the lambda expression.</param>
        /// <param name="parameters">Lambda parameter values.</param>
        /// <returns>Evaluated lambda expression.</returns>
        private object InvokeLambdaExpression(Lambda_expressionContext context, object[] parameters)
        {
            var lambdaBodyContext = context.lambda_body();
            if (lambdaBodyContext == null)
            {
                return null;
            }

            var parameterNames = VisitLambda_signature(context.lambda_signature()) as string[];

            // check if parameter name is not same as some local variable
            var parametersWithSameName = parameterNames.Where(name => localVariables.ContainsKey(name));
            if (parametersWithSameName.Any())
            {
                throw new ArgumentException($"Parameters {String.Join("; ", parametersWithSameName)} has same name as some local variables.");
            }

            var lambdaVisitor = new KSharpVisitor(evaluator, localVariables, parameterNames, parameters, Token);
            var result = lambdaVisitor.VisitLambda_body(lambdaBodyContext) as Tuple<IDictionary<string, object>, object>;

            // update variables which were changed within lambda
            UpdateLocalVariables(result.Item1);    
            
            return result.Item2;
        }

        
        /// <summary>
        /// Updates values of the local variables in the scope of lambda expression.
        /// </summary>
        /// <param name="lambdaLocals">Pairs of lambda local variables with their new value.</param>
        private void UpdateLocalVariables(IDictionary<string, object> lambdaLocals)
        {
            var newLocals = new Dictionary<string, object>();

            foreach (var tuple in localVariables)
            {
                lambdaLocals.TryGetValue(tuple.Key, out object newValue);

                newLocals.Add(tuple.Key, newValue);
            }

            localVariables = newLocals;
        }


        /// <summary>
        /// Initializes value for each local <paramref name="variables"/>.
        /// </summary>
        /// <param name="variables">Local variables to be initialized.</param>
        /// <param name="values">Values of the local variables</param>
        private void InitLocalVariables(string[] variables, object[] values)
        {
            if (variables.Length > values.Length)
            {
                throw new InvalidOperationException("At least one value has missing variable to which should assign to.");
            }
            else if (variables.Length < values.Length)
            {
                throw new InvalidOperationException("At least one variable has missing value.");
            }

            for (var i = 0; i < variables.Length; i++)
            {
                SetVariable(variables[i], values[i]);
            }
        }


        /// <summary>
        /// Initializes value for each global <paramref name="variables"/>.
        /// </summary>
        /// <param name="variables">Global variables to be initialized.</param>
        /// <param name="values">Values of the global variables</param>
        private void InitGlobalVariables(IDictionary<string, object> globalVariables)
        {
            foreach (var variable in globalVariables)
            {
                SetVariable(variable.Key, variable.Value);
            }
        }
        

        /// <summary>
        /// Evaluates whole expression given by <paramref name="context"/>.
        /// </summary>
        /// <returns>Evaluated whole expression.</returns>
        private object VisitTreeBody(Begin_expressionContext context)
        {
            // Evaluate statements
            var results = VisitStatement_list(context.statement_list()) as IList;

            var consoleOutput = evaluator.FlushOutput()?.ToString();
            if (consoleOutput != null)
            {
                if (results != null)
                {
                    results.Add(consoleOutput);
                }
                else
                {
                    results = new List<object>() { consoleOutput };
                }
            }

            return results;
        }


        /// <summary>
        /// Returns true, if <paramref name="item"/> is identifier.
        /// </summary>
        private bool IsIdentifier(object item)
        {
            return (item is string itemString) && !itemString.Contains("\"");
        }


        private object EvaluateMethodCall(string methodName, object[] arguments)
        {
            // if method is lambda, evaluate lambda expression, otherwise use evaluator
            if (localVariables.TryGetValue(methodName, out object lambdaContext))
            {
                return InvokeLambdaExpression(lambdaContext as Lambda_expressionContext, arguments as object[]);
            }

            return evaluator.InvokeMethod(methodName, arguments);
        }


        private object EvaluateMethod(string methodName, IParseTree argumentContext)
        {
            var arguments = VisitMethod_invocation(argumentContext as Method_invocationContext) as object[];

            return EvaluateMethodCall(methodName, arguments);
        }


        private object EvaluateIndexer(object collectionNameOrInstance, IParseTree context)
        {
            var index = VisitBracket_expression(context as Bracket_expressionContext);

            var collection = IsIdentifier(collectionNameOrInstance) ? GetVariable(collectionNameOrInstance as string) : collectionNameOrInstance;

            return evaluator.InvokeIndexer(collection, index);
        }
                   

        private object EvaluateAccessor(object accessedObject, IParseTree acessorContext, IParseTree argumentsContext)
        {
            var propertyOrMethodName = (string)VisitMember_access(acessorContext as Member_accessContext);
            var arguments = argumentsContext == null ? null :VisitMethod_invocation(argumentsContext as Method_invocationContext) as object[];

            if (IsIdentifier(accessedObject) && (string)accessedObject != String.Empty)
            {
                accessedObject = GetVariable(accessedObject.ToString());
            }

            return evaluator.InvokeMember(accessedObject, propertyOrMethodName, arguments);  
        }


        private void SetVariable(string name, object value)
        {
            localVariables[name] = value;
        }


        private object GetVariable(string name)
        {
            if (localVariables.TryGetValue(name, out object value))
            {
                return value;
            }
            else
            {
                return evaluator.GetVariableValue(name);
            }
        }


        /// <summary>
        /// Returns evaluated "+" operation between <paramref name="leftOperand"/> and <paramref name="rightOperand"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException" />
        /// <seealso cref="OperatorTypeEnum.PLUS"/>
        private object AddObjects(object leftOperand, object rightOperand)
        {
            var areIntegers = leftOperand is int && rightOperand is int;
            if (areIntegers)
            {
                return (int)leftOperand + (int)rightOperand;
            }

            var areNumeric = (leftOperand is int || leftOperand is decimal) && (rightOperand is int || rightOperand is decimal);
            if (areNumeric)
            {
                return Convert.ToDecimal(leftOperand) + Convert.ToDecimal(rightOperand);
            }
            
            if (leftOperand is DateTime)
            {
                if (rightOperand is TimeSpan)
                {
                    return ((DateTime)leftOperand).Add((TimeSpan)rightOperand);
                }
            }
            else if ((leftOperand is TimeSpan) && (rightOperand is TimeSpan))
            {
                return ((TimeSpan)leftOperand).Add((TimeSpan)rightOperand);
            }

            var areStrings = leftOperand is string || rightOperand is string;
            if (areStrings)
            {
                return String.Concat(leftOperand.ToString(), rightOperand.ToString());
            }

            throw new InvalidOperationException($"Objects of types {leftOperand.GetType().Name} and {rightOperand.GetType().Name} can not be added.");
        }


        /// <summary>
        /// Returns evaluated "-" operation between <paramref name="leftOperand"/> and <paramref name="rightOperand"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException" />
        /// <seealso cref="OperatorTypeEnum.MINUS"/>
        private object SubstractObjects(object leftOperand, object rightOperand)
        {
            var areIntegers = leftOperand is int && rightOperand is int;
            if (areIntegers)
            {
                return (int)leftOperand - (int)rightOperand;
            }

            var areNumeric = (leftOperand is int || leftOperand is decimal) && (rightOperand is int || rightOperand is decimal);
            if (areNumeric)
            {
                return Convert.ToDecimal(leftOperand) - Convert.ToDecimal(rightOperand);
            }

            if (leftOperand is DateTime)
            {
                if (rightOperand is DateTime)
                {
                    return ((DateTime)leftOperand).Subtract((DateTime)rightOperand);
                }

                if (rightOperand is TimeSpan)
                {
                    return ((DateTime)leftOperand).Subtract((TimeSpan)rightOperand);
                }
            }
            else if ((leftOperand is TimeSpan) && (rightOperand is TimeSpan))
            {
                return ((TimeSpan)leftOperand).Subtract((TimeSpan)rightOperand);
            }
            
            throw new InvalidOperationException($"Objects of types {leftOperand.GetType().Name} and {rightOperand.GetType().Name} can not be subtracted.");
        }
        

        /// <summary>
        /// Returns comparer based on type of <paramref name="leftOperand"/> and <paramref name="rightOperand"/>.
        /// </summary>
        /// <param name="leftOperand"></param>
        /// <param name="rightOperand"></param>
        private IComparer GetComparer(object leftOperand, object rightOperand)
        {
            Type leftType = leftOperand?.GetType();
            Type rightType = rightOperand?.GetType();

            if (leftType == null || rightType == null)
            {
                return new NullComparer();
            }

            var type = leftType;
            if (leftType != rightType) {

                if (IsNumeric(leftType) && IsNumeric(rightType))
                {
                    return new NumericComparer();
                }

                else if (rightType.IsAssignableFrom(leftType))
                {
                    type = rightType;
                }

                else if (leftType.IsAssignableFrom(rightType))
                {
                    type = leftType;
                }

                else
                {
                    throw new NotSupportedException($"Comparison of {leftType.GetType().Name} and {rightType.GetType().Name} is not supported");
                }
            }

            IComparer comparer = evaluator
                .KnownComparers
                .Where(comp => type.IsAssignableFrom(comp.Key))
                .FirstOrDefault()
                .Value
                ?? Comparer.Default;

            return comparer;
        }


        /// <summary>
        /// Returns true if given <paramref name="type"/> is number.
        /// </summary>
        private bool IsNumeric(Type type)
        {
            return mNumericTypes.Contains(type);
        }


        private bool IsGreater(object leftOperand, object rightOperand)
            => GetComparer(leftOperand, rightOperand).Compare(leftOperand, rightOperand) > 0;


        private bool IsGreaterOrEqual(object leftOperand, object rightOperand)
            => GetComparer(leftOperand, rightOperand).Compare(leftOperand, rightOperand) >= 0;


        private bool IsLess(object leftOperand, object rightOperand)
            => GetComparer(leftOperand, rightOperand).Compare(leftOperand, rightOperand) < 0;


        private bool IsLessOrEqual(object leftOperand, object rightOperand)
            => GetComparer(leftOperand, rightOperand).Compare(leftOperand, rightOperand) <= 0;


        private bool IsEqual(object leftOperand, object rightOperand)
            => GetComparer(leftOperand, rightOperand).Compare(leftOperand, rightOperand) == 0;


        private bool IsUnequal(object leftOperand, object rightOperand) 
            => !IsEqual(leftOperand, rightOperand);


        /// <summary>
        /// Saves the parameter to context. 
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns><c>null</c></returns>
        public override object VisitParameter([NotNull] ParameterContext context)
        {
            var parameterName = VisitParameter_name(context.parameter_name()) as string;
            object parameterValue = null;

            var parameterValueContext = context.parameter_value();
            if (parameterValueContext != null)
            {
                parameterValue = VisitParameter_value(parameterValueContext);
            }

            evaluator.SaveParameter(parameterName, parameterValue);

            return null;
        }


        /// <summary>
        /// Returns the value of the parameter.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Value of the parameter.</returns>
        public override object VisitParameter_value([NotNull] Parameter_valueContext context)
        {            
            if (context.GetChild(0) is LiteralContext)
            {
                return VisitLiteral(context.literal());
            }

            return context.GetText();
        }


        /// <summary>
        /// Returns the name of the parameter.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Name of the parameter.</returns>
        public override object VisitParameter_name([NotNull] Parameter_nameContext context)
        {
            return context.GetText();
        }


        /// <summary>
        /// Evaluates string literal expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Evaluated string literal.</returns>
        public override object VisitString_literal([NotNull] String_literalContext context)
        {
            var verb = context.VERBATIUM_STRING();
            if (verb != null)
            {
                return verb.GetText().Trim('@'); ;
            }

            return context.GetText();
        }


        /// <summary>
        /// Evaluates boolean expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Evaluated boolean value.</returns>
        public override object VisitBoolean_literal([NotNull] Boolean_literalContext context) 
            => Convert.ToBoolean(context.GetText());


        /// <summary>
        /// Evaluates percent expression. 
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Evaluated decimal value.</returns>
        public override object VisitPercent_literal([NotNull] Percent_literalContext context)
        {
            var percentValue = context.INTEGER_LITERAL() ?? context.REAL_LITERAL();

            var stringValue = percentValue.GetText();

            return Convert.ToDecimal(stringValue) / 100;
        }


        /// <summary>
        /// Evaluates literal expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Value of correct type.</returns>
        public override object VisitLiteral([NotNull] LiteralContext context)
        {
            var simpleLiteral = context.INTEGER_LITERAL();
            if (simpleLiteral != null)
            {
                return Int32.Parse(simpleLiteral.GetText());
            }

            simpleLiteral = context.REAL_LITERAL();
            if (simpleLiteral != null)
            {
                return Decimal.Parse(simpleLiteral.GetText(), System.Globalization.NumberStyles.Any);
            }

            simpleLiteral = context.CHARACTER_LITERAL();
            if (simpleLiteral != null)
            {
                return simpleLiteral.GetText();
            }

            simpleLiteral = context.DATE();
            if (simpleLiteral != null)
            {
                return DateTime.Parse(simpleLiteral.GetText());
            }

            simpleLiteral = context.GUID();
            if (simpleLiteral != null)
            {
                return Guid.Parse(simpleLiteral.GetText());
            }

            ParserRuleContext complexLiteral = context.percent_literal();
            if (complexLiteral != null)
            {
                return VisitPercent_literal(complexLiteral as Percent_literalContext);
            }

            complexLiteral = context.boolean_literal();
            if (complexLiteral != null)
            {
                return VisitBoolean_literal(complexLiteral as Boolean_literalContext);
            }

            complexLiteral = context.string_literal();
            if (complexLiteral != null)
            {
                return VisitString_literal(complexLiteral as String_literalContext);
            }

            return null;
        }


        /// <summary>
        /// Sets a local value according to specified assignment operator.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns><c>null</c></returns>
        public override object VisitAssignment([NotNull] AssignmentContext context)
        {
            var variableName = context.IDENTIFIER().GetText();
            var variableOriginalValue = GetVariable(variableName);

            // i++, ++i, i--, --i
            if (context.INC() != null || context.DEC() != null)
            {
                if (variableOriginalValue == null)
                {
                    throw new InvalidOperationException($"Cannot apply any operation on variable '{variableName}' without assigned value.");
                }

                var value = Convert.ToInt32(variableOriginalValue);
                if (context.INC() != null)
                {
                    ++value;
                }
                else
                {
                    --value;
                }

                SetVariable(variableName, value);

                return value;
            }
            
            // =, +=, -=, *=, ...
            var assignableContext = context.assignable_expression();
            if (assignableContext == null)
            {
                return null;
            }

            var assignableValue = VisitAssignable_expression(assignableContext);

            var operationType = this.MatchAssignmentOperator(context.assignment_operator());

            if ((operationType != OperatorTypeEnum.ASSIGN) && (variableOriginalValue == null))
            {
                throw new InvalidOperationException($"Cannot apply any operation on variable '{variableName}' without assigned value.");
            }

            switch (operationType)
            {
                case OperatorTypeEnum.PLUS_ASSIGN:
                    assignableValue = AddObjects(variableOriginalValue, assignableValue);
                    break;
                case OperatorTypeEnum.MINUS_ASSIGN:
                    assignableValue = SubstractObjects(variableOriginalValue, assignableValue);
                    break;
                case OperatorTypeEnum.MULTIPLY_ASSIGN:
                    assignableValue = Convert.ToDecimal(variableOriginalValue) * Convert.ToDecimal(assignableValue);
                    break;
                case OperatorTypeEnum.DIVIDE_ASSIGN:
                    assignableValue = Convert.ToDecimal(variableOriginalValue) / Convert.ToDecimal(assignableValue);
                    break;
                case OperatorTypeEnum.MODULO_ASSIGN:
                    assignableValue = Convert.ToDecimal(variableOriginalValue) % Convert.ToDecimal(assignableValue);
                    break;
                case OperatorTypeEnum.AND_ASSIGN:
                    assignableValue = Convert.ToBoolean(variableOriginalValue) && Convert.ToBoolean(assignableValue);
                    break;
                case OperatorTypeEnum.OR_ASSIGN:
                    assignableValue = Convert.ToBoolean(variableOriginalValue) || Convert.ToBoolean(assignableValue);
                    break;
                case OperatorTypeEnum.XOR_ASSIGN:
                    assignableValue = Convert.ToBoolean(variableOriginalValue) ^ Convert.ToBoolean(assignableValue);
                    break;
                case OperatorTypeEnum.LEFT_SHIFT_ASSIGN:
                    assignableValue = Convert.ToInt32(variableOriginalValue) << Convert.ToInt32(assignableValue);
                    break;
                case OperatorTypeEnum.RIGHT_SHIFT_ASSIGN:
                    assignableValue = Convert.ToInt32(variableOriginalValue) >> Convert.ToInt32(assignableValue);
                    break;
            }

            SetVariable(variableName, assignableValue);

            return null;
        }
        

        /// <summary>
        /// Evaluates "??" operator.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Left value if it is not null, right value otherwise.</returns>
        public override object VisitNull_coalescing_expression([NotNull] Null_coalescing_expressionContext context)
        {
            var leftOperand = VisitOr_expression(context.or_expression());

            var rightOperandContext = context.null_coalescing_expression();
            if (rightOperandContext == null)
            {
                return leftOperand;
            }

            return leftOperand ?? VisitNull_coalescing_expression(rightOperandContext);
        }


        /// <summary>
        /// Evaluates ternary operator.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Left value if condition is <c>true</c>, otherwise it returns the right value.</returns>
        public override object VisitTernary_expression([NotNull] Ternary_expressionContext context)
        {
            var condition = VisitNull_coalescing_expression(context.null_coalescing_expression());

            var leftAlternativeContext = context.embedded_statement(0);
            if (leftAlternativeContext == null)
            {
                return condition;
            }

            return VisitEmbedded_statement((bool) condition ? leftAlternativeContext : context.embedded_statement(1));
        }


        /// <summary>
        /// Evaluates additive expression. 
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Result of addition.</returns>
        public override object VisitAdd_expression([NotNull] Add_expressionContext context) 
            => EvaluateAllAlternatives(context, AddObjects, (Func<object, object>)null);


        /// <summary>
        /// Evaluates subtracts expression. 
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Result of subtraction.</returns>
        public override object VisitSubstract_expression([NotNull] Substract_expressionContext context) 
            => EvaluateAllAlternatives(context, SubstractObjects, (Func<object, object>)null);


        /// <summary>
        /// Evaluates multiplicative expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Result of multiplication.</returns>
        public override object VisitMultiplicative_expression([NotNull] Multiplicative_expressionContext context) 
            => EvaluateAllAlternatives(context, (left, right) => left * right, Convert.ToDecimal);


        /// <summary>
        /// Evaluates division.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Result of division.</returns>
        public override object VisitDivide_expression([NotNull] Divide_expressionContext context) 
            => EvaluateAllAlternatives(context, (left, right) => left / right, Convert.ToDecimal);


        /// <summary>
        /// Evaluates modulo.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Result of modulo.</returns>
        public override object VisitModulo_expression([NotNull] Modulo_expressionContext context) 
            => EvaluateAllAlternatives(context, (left, right) => left % right, Convert.ToDecimal);


        /// <summary>
        /// Evaluates equality expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Result of equality.</returns>
        public override object VisitEquality_expression([NotNull] Equality_expressionContext context) 
            => EvaluateAllAlternatives(context, IsEqual, (Func<object, object>)null);


        /// <summary>
        /// Evaluates inequality expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Result of inequality.</returns>
        public override object VisitUnequality_expression([NotNull] Unequality_expressionContext context) 
            => EvaluateAllAlternatives(context, IsUnequal, (Func<object, object>)null);


        /// <summary>
        /// Evaluates > expression. 
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitGreater_than_expression([NotNull] Greater_than_expressionContext context) 
            => EvaluateAllAlternatives(context, IsGreater, (Func<object, object>)null);


        /// <summary>
        /// Evaluates >= expression. 
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitGreater_than_or_equal_expression([NotNull] Greater_than_or_equal_expressionContext context) 
            => EvaluateAllAlternatives(context, IsGreaterOrEqual, (Func<object, object>)null);


        /// <summary>
        /// Evaluates &lt; expression. 
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitLess_than_expression([NotNull] Less_than_expressionContext context) 
            => EvaluateAllAlternatives(context, IsLess, (Func<object, object>)null);


        /// <summary>
        /// Evaluates &lt;= expression. 
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitLess_than_or_equal_expression([NotNull] Less_than_or_equal_expressionContext context) 
            => EvaluateAllAlternatives(context, IsLessOrEqual, (Func<object, object>)null);


        /// <summary>
        /// Evaluates logical or.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitOr_expression([NotNull] Or_expressionContext context) 
            => EvaluateAllAlternatives(context, (left, right) => left || right, Convert.ToBoolean);


        /// <summary>
        /// Evaluates logical XOR.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitXor_expression([NotNull] Xor_expressionContext context) 
            => EvaluateAllAlternatives(context, (left, right) => left ^ right, Convert.ToBoolean);


        /// <summary>
        /// Evaluates logical and.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitAnd_expression([NotNull] And_expressionContext context) 
            => EvaluateAllAlternatives(context, (left, right) => left && right, Convert.ToBoolean);


        /// <summary>
        /// Evaluates left shift expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitLeft_shift_expression([NotNull] Left_shift_expressionContext context)
        {
            return EvaluateAllAlternatives(context, (left, right) => left << right, Convert.ToInt32);
        }


        /// <summary>
        /// Evaluates right shift expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitRight_shift_expression([NotNull] Right_shift_expressionContext context) 
            => EvaluateAllAlternatives(context, (left, right) => left >> right, Convert.ToInt32);


        /// <summary>
        /// Evaluates if statement.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitIf_expression([NotNull] If_expressionContext context)
        {
            var condition = (bool)VisitTernary_expression(context.ternary_expression());
            if (condition)
            {
                return VisitBlock(context.block(0));
            }

            var alternative = context.block(1);
            if (alternative != null)
            {
                return VisitBlock(alternative);
            }

            return null;
        }


        /// <summary>
        /// Evaluates for loop.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitFor_expression([NotNull] For_expressionContext context)
        {
            VisitFor_initializer(context.for_initializer());

            List<object> results = new List<object>();

            var conditionContext = context.ternary_expression();
            bool conditionResult = (bool) VisitTernary_expression(conditionContext);

            while (conditionResult)
            {
                Token.ThrowIfCancellationRequested();

                if (VisitBlock(context.block()) is List<object> blockResult)
                {
                    blockResult.ForEach(subResult => results.Add(subResult));
                }

                if (breakLoop || returnFromLoop)
                {
                    breakLoop = false;
                    returnFromLoop = false;
                    break;
                }
                else if (continueLoop)
                {
                    continueLoop = false;
                    continue;
                }

                VisitFor_iterator(context.for_iterator());
                conditionResult = (bool)VisitTernary_expression(conditionContext);
            }

            return results.Any() ? results : null;
        }


        /// <summary>
        /// Evaluates while loop.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitWhile_expression([NotNull] While_expressionContext context)
        {            
            List<object> results = new List<object>();

            var conditionContext = context.ternary_expression();
            bool conditionResult = (bool)VisitTernary_expression(conditionContext);

            while (conditionResult)
            {
                Token.ThrowIfCancellationRequested();

                if (VisitBlock(context.block()) is List<object> blockResult)
                {
                    blockResult.ForEach(subResult => results.Add(subResult));
                }

                if (breakLoop || returnFromLoop)
                {
                    breakLoop = false;
                    returnFromLoop = false;
                    break;
                }
                else if (continueLoop)
                {
                    continueLoop = false;
                    continue;
                }

                conditionResult = (bool)VisitTernary_expression(conditionContext);
            }

            return results.Any() ? results : null;
        }


        /// <summary>
        /// Evaluates foreach loop.
        /// </summary>
        /// <param name="context"></param>
        /// <returns>List of statement results.</returns>
        public override object VisitForeach_expression([NotNull] Foreach_expressionContext context)
        {
            List<object> results = new List<object>();
            var identifierName = context.IDENTIFIER().GetText();

            object possibleCollection = VisitUnary_expression(context.unary_expression());
            IList collection = possibleCollection as IList;

            if (possibleCollection is string)
            {
                collection = ((string)possibleCollection).ToCharArray();
            }

            foreach (object item in collection)
            {
                Token.ThrowIfCancellationRequested();

                SetVariable(identifierName, item);

                if (VisitBlock(context.block()) is List<object> blockResult)
                {
                    blockResult.ForEach(subResult => results.Add(subResult));
                }

                if (breakLoop || returnFromLoop)
                {
                    breakLoop = false;
                    returnFromLoop = false;
                    break;
                }
                else if (continueLoop)
                {
                    continueLoop = false;
                    continue;
                }

            }

            localVariables.Remove(identifierName);

            return results.Any() ? results : null; ;
        }


        /// <summary>
        /// Evaluates break, continue, return by setting private properties, and checking them later.
        /// </summary>
        /// <param name="context"></param>
        /// <returns><c>Null</c> or evaluated statement which follows return.</returns>
        public override object VisitJump_statement([NotNull] Jump_statementContext context)
        {
            if (context.BREAK() != null)
            {
                breakLoop = true;
            }
            else if (context.CONTINUE() != null)
            {
                continueLoop = true;
            }
            else
            {
                returnFromLoop = true;

                var returnContext = context.ternary_expression();
                return returnContext == null ? null : VisitTernary_expression(returnContext);
            }

            return null;
        }


        /// <summary>
        /// Evaluates lambda expression. Returns the context of expression as it will be evaluated later. 
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns><see cref="Lambda_expressionContext"/> instance.</returns>
        public override object VisitLambda_expression([NotNull] Lambda_expressionContext context) 
            => context;


        /// <summary>
        /// Returns array of lambda parameter names.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Array of lambda parameter names.</returns>
        public override object VisitLambda_signature([NotNull] Lambda_signatureContext context)
        {
            string[] result = new string[0];
            var identifier = context.IDENTIFIER();
            if (identifier != null)
            {
                result = new string[]{ identifier.GetText()};
            }
            else {

                var paramaterList = context.lambda_signature_parameter_list();
                if (paramaterList != null)
                {
                    result = VisitLambda_signature_parameter_list(paramaterList) as string[];
                }
            }

            return result;
        }


        /// <summary>
        /// Evaluates the body of lambda expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Local variables and the result of evaluation.</returns>
        public override object VisitLambda_body([NotNull] Lambda_bodyContext context)
        {
            object result = null;

            ParserRuleContext bodyContext = context.statement_list();
            if (bodyContext != null)
            {
                result = VisitStatement_list(bodyContext as Statement_listContext);
            }

            bodyContext = context.expression();
            if (bodyContext != null)
            {
                result = VisitExpression(bodyContext as ExpressionContext);
            }

            bodyContext = context.block();
            if (bodyContext != null)
            {
                result = VisitBlock(bodyContext as BlockContext);
            }

            return new Tuple<IDictionary<string, object>, object>(localVariables, result);
        }


        /// <summary>
        /// Evaluates the names of lambda parameters.
        /// </summary>
        /// <param name="context"></param>
        /// <returns>List of parameter names.</returns>
        public override object VisitLambda_signature_parameter_list([NotNull] Lambda_signature_parameter_listContext context) 
            => context.IDENTIFIER().Select(parameter => parameter.GetText()).ToArray();


        /// <summary>
        /// Extracts arguments of a method call.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Arguments of a method call.</returns>
        public override object VisitMethod_invocation([NotNull] Method_invocationContext context)
         {
            var argumentListContext = context.argument_list();
            if (argumentListContext != null)
            {
                return VisitArgument_list(argumentListContext);
            }

            return new string[0];
         }


        /// <summary>
        /// Extracts arguments of a method call.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Arguments of a method call.</returns>
        public override object VisitArgument_list([NotNull] Argument_listContext context)
        {
            var argumentContext = context.argument();
            if (argumentContext == null)
            {
                return new string[0];
            }

            List<object> arguments = new List<object>();
            foreach (var argument in argumentContext)
            {
                var argumentResult = VisitArgument(argument);                
                arguments.Add(argumentResult);
            }

            return arguments.ToArray();
        }


        /// <summary>
        /// Evaluates parentheses expression.
        /// </summary>
        /// <param name="context"></param>
        /// <returns>Expression result.</returns>
        public override object VisitParentheses_expression([NotNull] Parentheses_expressionContext context) 
            => VisitExpression(context.expression());


        /// <summary>
        /// Evaluates expression.
        /// </summary>
        /// <param name="context"></param>
        /// <returns>Expression result.</returns>
        public override object VisitExpression([NotNull] ExpressionContext context)
        {
            ParserRuleContext innerContext = context.non_assignment_expression();
            if (innerContext != null)
            {
                return VisitNon_assignment_expression(innerContext as Non_assignment_expressionContext);
            }

            innerContext = context.assignment();
            if (innerContext != null)
            {
                VisitAssignment(innerContext as AssignmentContext);
            }

            return null;            
        }


        /// <summary>
        /// Initial expression. Saves parameters and then evaluates the expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitBegin_expression([NotNull] Begin_expressionContext context)
        {
            var length = context.parameter().Length;
            for (int i = 0; i < length; i++)
            {
                VisitParameter(context.parameter(i));
            }

            cancellationToken = cancellationToken ?? evaluator.GetCancellationToken();
            Token.ThrowIfCancellationRequested();

            return VisitTreeBody(context);
        }

        
        /// <summary>
        /// Evaluates all statements.
        /// </summary>
        /// <param name="context"></param>
        /// <returns>List of statement results.</returns>
        public override object VisitStatement_list([NotNull] Statement_listContext context)
        {
            List<object> results = new List<object>();

            string consoleOutput = null;

            var statements = context.statement();
            foreach (var statement in statements)
            {
                var statementResult = VisitStatement(statement);
                if (statementResult != null) {

                    consoleOutput = evaluator.FlushOutput()?.ToString();

                    if (statementResult is List<object>)
                    {
                        if (consoleOutput != null)
                        {
                            var resultItem = String.Concat(consoleOutput, statementResult.ToString());
                            results.Add(resultItem);
                        }
                        else
                        {
                            (statementResult as List<object>).ForEach(subResult => results.Add(subResult));
                        }
                    }
                    else
                    {
                        if (consoleOutput != null && consoleOutput != String.Empty)
                        {
                            var resultItem = String.Concat(consoleOutput, statementResult.ToString());
                            results.Add(resultItem);
                        }
                        else
                        {
                            results.Add(statementResult);
                        }
                    }
                }

                // in case a jump statement occurred, stop processing statements
                if (breakLoop || returnFromLoop || continueLoop)
                {
                    break;
                }
            }

            return results.Any() ? results : null;
        }


        /// <summary>
        /// Evaluates block.
        /// </summary>
        /// <param name="context"></param>
        /// <returns>Expression result.</returns>
        public override object VisitBlock([NotNull] BlockContext context) 
            => VisitStatement_list(context.statement_list());


        /// <summary>
        /// Evaluates primary expression. Primary expression can be anything from a simple literal or identifier to a method call, property accessors, array indexer.
        /// </summary>
        /// <param name="context"></param>
        /// <returns>Expression result.</returns>
        public override object VisitPrimary_expression([NotNull] Primary_expressionContext context)
        {
            object expStart = VisitPrimary_expression_start(context.primary_expression_start());
            bool canBeIdentifier = true;

            int numberOfChilren = context.children.Count;                     
            for (var i = 1; i < numberOfChilren; i++)
            {
                // if the expression is more than just a simple expression, the evaluated value will never be identifier
                canBeIdentifier = false;

                var subExpressionContext = context.GetChild(i);
                if (subExpressionContext == null)
                {
                    canBeIdentifier = true;
                    break;
                }

                // collection[5] or collection["key"]
                if (subExpressionContext is Bracket_expressionContext)
                {
                    expStart = EvaluateIndexer(expStart, subExpressionContext);
                }

                // member.property.access
                else if (subExpressionContext is Member_accessContext)
                {
                    var nextChildContext = context.GetChild(i + 1);
                    if (nextChildContext != null && nextChildContext is Method_invocationContext)
                    {
                        expStart = EvaluateAccessor(expStart, subExpressionContext, nextChildContext);
                        i++;
                    }
                    else
                    {
                        expStart = EvaluateAccessor(expStart, subExpressionContext, null);
                    }
                }

                // method(param1, ..)
                else if (subExpressionContext is Method_invocationContext)
                {
                    expStart = EvaluateMethod((string)expStart, subExpressionContext);
                }

            }
            
            if (IsIdentifier(expStart) && canBeIdentifier)
            {
                return GetVariable(expStart as string);
            }
            
            // remove quotes from strings
            if (expStart is string)
            {
                return ((string)expStart).Trim('\"');
            }

            return expStart;
        }



        /// <summary>
        /// Evaluates the accessors name.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitMember_access([NotNull] Member_accessContext context) 
            => context.IDENTIFIER().GetText();


        /// <summary>
        /// Evaluates the indexer.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitBracket_expression([NotNull] Bracket_expressionContext context)
        {
            var indexerArgumentContext = context.indexer_argument();
            object leftMostIndexer = VisitIndexer_argument(indexerArgumentContext[0]);
            
            int accessLevels = indexerArgumentContext.Length;
            for (var i = 1; i < accessLevels; i++)
            {
                string accessorName = (string)VisitIndexer_argument(indexerArgumentContext[i]);
                leftMostIndexer = leftMostIndexer
                    .GetType()
                    .GetProperty(accessorName)
                    .GetValue(leftMostIndexer, null);
            }

            return leftMostIndexer;
        }
        

        /// <summary>
        /// Evaluates the start of the expression.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitPrimary_expression_start([NotNull] Primary_expression_startContext context)
        {
            ParserRuleContext innerContext = context.parentheses_expression();
            if (innerContext != null)
            {
                return VisitParentheses_expression(innerContext as Parentheses_expressionContext);
            }

            innerContext = context.literal();
            if (innerContext != null)
            {
                return VisitLiteral(innerContext as LiteralContext);
            }

            return context.IDENTIFIER().GetText();
        }


        /// <summary>
        /// Evaluates primary expressions and applies operators to unary expressions.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitUnary_expression([NotNull] Unary_expressionContext context)
        {
            var primaryContext = context.primary_expression();
            if (primaryContext != null)
            {
                return VisitPrimary_expression(primaryContext);
            }

            var unaryContext = context.unary_expression();
            if (unaryContext == null)
            {
                return null;
            }

            object unaryValue = VisitUnary_expression(unaryContext);
            OperatorTypeEnum operatorType = this.MatchUnaryOperator(context);
            switch (operatorType)
            { 
                case OperatorTypeEnum.MINUS:
                    return Convert.ToDecimal(unaryValue) * (-1); 

                case OperatorTypeEnum.BANG:
                    return !Convert.ToBoolean(unaryValue);

                case OperatorTypeEnum.PLUS:
                case OperatorTypeEnum.NONE:
                default:
                    return Convert.ToDecimal(unaryValue);
            }           
        }


        /// <summary>
        /// Evaluates embedded statement.
        /// </summary>
        /// <param name="context">Context of the parser rule.</param>
        /// <returns>Expression result.</returns>
        public override object VisitEmbedded_statement([NotNull] Embedded_statementContext context)
        {
            ParserRuleContext innerContext = context.expression();
            if (innerContext != null)
            {
                return VisitExpression(innerContext as ExpressionContext);
            }

            innerContext = context.block();
            if (innerContext != null)
            {
                return VisitBlock(innerContext as BlockContext);
            }

            return null;            
        }
    }
}
