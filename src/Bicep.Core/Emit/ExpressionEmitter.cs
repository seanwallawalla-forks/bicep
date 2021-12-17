// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Azure.Deployments.Expression.Configuration;
using Azure.Deployments.Expression.Expressions;
using Azure.Deployments.Expression.Serializers;
using Bicep.Core.CodeAnalysis;
using Bicep.Core.Semantics;
using Bicep.Core.Semantics.Metadata;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem.Az;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bicep.Core.Emit
{
    public class ExpressionEmitter
    {
        private static readonly ExpressionSerializer ExpressionSerializer = new(new ExpressionSerializerSettings
        {
            IncludeOuterSquareBrackets = true,

            // this setting will ensure that we emit strings instead of a string literal expressions
            SingleStringHandling = ExpressionSerializerSingleStringHandling.SerializeAsString
        });

        private readonly JsonTextWriter writer;
        private readonly EmitterContext context;
        private readonly ExpressionConverter converter;

        public ExpressionEmitter(JsonTextWriter writer, EmitterContext context)
        {
            this.writer = writer;
            this.context = context;
            this.converter = new ExpressionConverter(context);
        }

        private Operation GetExpressionOperation(SyntaxBase syntax)
        {
            var symbol = context.SemanticModel.GetSymbolInfo(syntax);
            if (symbol is VariableSymbol variableSymbol && context.VariablesToInline.Contains(variableSymbol))
            {
                return GetExpressionOperation(variableSymbol.Value);
            }

            if (syntax is FunctionCallSyntax functionCall &&
                symbol is FunctionSymbol functionSymbol &&
                string.Equals(functionSymbol.Name, LanguageConstants.AnyFunction, LanguageConstants.IdentifierComparison))
            {
                // the outermost function in the current syntax node is the "any" function
                // we should emit its argument directly
                // otherwise, they'd get wrapped in a json() template function call in the converted expression

                // we have checks for function parameter count mismatch, which should prevent an exception from being thrown
                return GetExpressionOperation(functionCall.Arguments.Single().Expression);
            }

            switch (syntax)
            {
                case BooleanLiteralSyntax boolSyntax:
                    return new ConstantValueOperation(boolSyntax.Value);

                case IntegerLiteralSyntax integerSyntax:
                    return new ConstantValueOperation(integerSyntax.Value);

                case NullLiteralSyntax _:
                    return new NullValueOperation();

                case ObjectSyntax objectSyntax:
                    var properties = objectSyntax.Properties.Select(p => new ObjectPropertyOperation(
                        p.TryGetKeyText() is {} keyName ? new ConstantValueOperation(keyName) : GetExpressionOperation(p.Key),
                        GetExpressionOperation(p.Value)));

                    return new ObjectOperation(properties.ToImmutableArray());

                case ArraySyntax arraySyntax:
                    var items = arraySyntax.Items.Select(x => GetExpressionOperation(x.Value));
                    return new ArrayOperation(items.ToImmutableArray());

                case ForSyntax forSyntax:
                    return new ForLoopOperation(
                        GetExpressionOperation(forSyntax.Expression),
                        forSyntax.ItemVariable!,
                        forSyntax.IndexVariable,
                        GetExpressionOperation(forSyntax.Body));

                case ParenthesizedExpressionSyntax _:
                case UnaryOperationSyntax _:
                case BinaryOperationSyntax _:
                case TernaryOperationSyntax _:
                case StringSyntax _:
                case InstanceFunctionCallSyntax _:
                case FunctionCallSyntax _:
                case ArrayAccessSyntax _:
                case PropertyAccessSyntax _:
                case ResourceAccessSyntax _:
                case VariableAccessSyntax _:
                    return converter.ConvertExpressionOperation(syntax);

                default:
                    throw new NotImplementedException($"Cannot emit unexpected expression of type {syntax.GetType().Name}");
            }
        }

        public void EmitExpression(SyntaxBase syntax)
            => EmitOperation(GetExpressionOperation(syntax));

        private void EmitOperation(Operation operation)
        {
            switch (operation)
            {
                case ConstantValueOperation constantValueOperation when constantValueOperation.Value is bool boolValue:
                    writer.WriteValue(boolValue);
                    break;

                case ConstantValueOperation constantValueOperation when constantValueOperation.Value is long intValue:
                    writer.WriteValue(intValue);
                    break;

                case NullValueOperation _:
                    writer.WriteNull();
                    break;

                case ObjectOperation objectOperation:
                    writer.WriteStartObject();
                    EmitObjectProperties(objectOperation);
                    writer.WriteEndObject();
                    break;

                case ArrayOperation arrayOperation:
                    writer.WriteStartArray();
                    foreach (var item in arrayOperation.Items)
                    {
                        EmitOperation(item);
                    }
                    writer.WriteEndArray();
                    break;

                default:
                    EmitLanguageOperation(operation);
                    break;
            }
        }

        public void EmitExpression(SyntaxBase resourceNameSyntax, SyntaxBase? indexExpression, SyntaxBase newContext)
        {
            var converterForContext = converter.CreateConverterForIndexReplacement(resourceNameSyntax, indexExpression, newContext);

            var expression = converterForContext.ConvertExpression(resourceNameSyntax);
            var serialized = ExpressionSerializer.SerializeExpression(expression);

            writer.WriteValue(serialized);
        }

        public void EmitUnqualifiedResourceId(ResourceMetadata resource, SyntaxBase? indexExpression, SyntaxBase newContext)
        {
            var converterForContext = converter.CreateConverterForIndexReplacement(resource.NameSyntax, indexExpression, newContext);

            var unqualifiedResourceId = converterForContext.GetUnqualifiedResourceId(resource);
            var serialized = ExpressionSerializer.SerializeExpression(unqualifiedResourceId);

            writer.WriteValue(serialized);
        }

        public void EmitIndexedSymbolReference(ResourceMetadata resource, SyntaxBase indexExpression, SyntaxBase newContext)
        {
            var replacementContext = converter.TryGetReplacementContext(resource.NameSyntax, indexExpression, newContext);
            var expression = converter.GenerateSymbolicReference(resource.Symbol.Name, replacementContext);

            writer.WriteValue(ExpressionSerializer.SerializeExpression(expression));
        }

        public void EmitIndexedSymbolReference(ModuleSymbol moduleSymbol, SyntaxBase indexExpression, SyntaxBase newContext)
        {
            var replacementContext = converter.TryGetReplacementContext(ExpressionConverter.GetModuleNameSyntax(moduleSymbol), indexExpression, newContext);
            var expression = converter.GenerateSymbolicReference(moduleSymbol.Name, replacementContext);

            writer.WriteValue(ExpressionSerializer.SerializeExpression(expression));
        }

        public void EmitResourceIdReference(ResourceMetadata resource, SyntaxBase? indexExpression, SyntaxBase newContext)
        {
            var converterForContext = this.converter.CreateConverterForIndexReplacement(resource.NameSyntax, indexExpression, newContext);

            var resourceIdExpression = converterForContext.GetFullyQualifiedResourceId(resource);
            var serialized = ExpressionSerializer.SerializeExpression(resourceIdExpression);

            writer.WriteValue(serialized);
        }

        public void EmitResourceIdReference(ModuleSymbol moduleSymbol, SyntaxBase? indexExpression, SyntaxBase newContext)
        {
            var converterForContext = this.converter.CreateConverterForIndexReplacement(ExpressionConverter.GetModuleNameSyntax(moduleSymbol), indexExpression, newContext);

            var resourceIdExpression = converterForContext.GetFullyQualifiedResourceId(moduleSymbol);
            var serialized = ExpressionSerializer.SerializeExpression(resourceIdExpression);

            writer.WriteValue(serialized);
        }

        public LanguageExpression GetFullyQualifiedResourceName(ResourceMetadata resource)
        {
            return converter.GetFullyQualifiedResourceName(resource);
        }

        public LanguageExpression GetManagementGroupResourceId(SyntaxBase managementGroupNameProperty, SyntaxBase? indexExpression, SyntaxBase newContext, bool fullyQualified)
        {
            var converterForContext = converter.CreateConverterForIndexReplacement(managementGroupNameProperty, indexExpression, newContext);
            return converterForContext.GenerateManagementGroupResourceId(managementGroupNameProperty, fullyQualified);
        }

        private void EmitLanguageOperation(Operation operation)
        {
            if (operation is ConstantValueOperation constantValueOperation && constantValueOperation.Value is long intValue)
            {
                // the converted expression is an integer literal

                // for integer literals the expression will look like "[42]" or "[-12]"
                // while it's still a valid template expression that works in ARM, it looks weird
                // and is also not recognized by the template language service in VS code
                // let's serialize it as a proper integer instead
                writer.WriteValue(intValue);

                return;
            }

            // strings literals and other expressions must be processed with the serializer to ensure correct conversion and escaping
            var converted = converter.ConvertOperation(operation);
            var serialized = ExpressionSerializer.SerializeExpression(converted);

            writer.WriteValue(serialized);
        }

        public void EmitCopyObject(string? name, ForLoopOperation @for, Operation? input, string? copyIndexOverride = null, long? batchSize = null)
        {
            // local function
            static bool CanEmitAsInputDirectly(Operation input)
            {
                // the deployment engine only allows JTokenType of String or Object in the copy loop "input" property
                // everything else must be converted into an expression
                return input switch
                {
                    // objects should be emitted as is
                    ObjectOperation => true,

                    // constant values should be emitted as-is
                    ConstantValueOperation => true,

                    // all other expressions should be converted into a language expression before emitting
                    // which will have the resulting JTokenType of String
                    _ => false
                };
            }

            writer.WriteStartObject();

            if (name is not null)
            {
                this.EmitProperty("name", name);
            }

            // construct the length ARM expression from the Bicep array expression
            // type check has already ensured that the array expression is an array
            this.EmitPropertyWithTransform(
                "count",
                @for.Expression,
                arrayExpression => new FunctionExpression("length", new[] { arrayExpression }, Array.Empty<LanguageExpression>()));

            if (batchSize.HasValue)
            {
                this.EmitProperty("mode", "serial");
                this.EmitProperty("batchSize", () => writer.WriteValue(batchSize.Value));
            }

            if (input != null)
            {
                if (copyIndexOverride == null)
                {
                    if (CanEmitAsInputDirectly(input))
                    {
                        this.EmitProperty("input", () => EmitOperation(input));
                    }
                    else
                    {
                        this.EmitPropertyWithTransform("input", input, converted => ExpressionConverter.ToFunctionExpression(converted));
                    }
                }
                else
                {
                    this.EmitPropertyWithTransform("input", input, expression =>
                    {
                        if (!CanEmitAsInputDirectly(input))
                        {
                            expression = ExpressionConverter.ToFunctionExpression(expression);
                        }

                        // the named copy index in the serialized expression is incorrect
                        // because the object syntax here does not match the JSON equivalent due to the presence of { "value": ... } wrappers
                        // for now, we will manually replace the copy index in the converted expression
                        // this approach will not work for nested property loops
                        var visitor = new LanguageExpressionVisitor
                        {
                            OnFunctionExpression = function =>
                            {
                                if (string.Equals(function.Function, "copyIndex") &&
                                    function.Parameters.Length == 1 &&
                                    function.Parameters[0] is JTokenExpression)
                                {
                                    // it's an invocation of the copyIndex function with 1 argument with a literal value
                                    // replace the argument with the correct value
                                    function.Parameters = new LanguageExpression[] { new JTokenExpression("value") };
                                }
                            }
                        };

                        // mutate the expression
                        expression.Accept(visitor);

                        return expression;
                    });
                }
            }

            writer.WriteEndObject();
        }

        public void EmitCopyObject(string? name, ForSyntax syntax, SyntaxBase? input, string? copyIndexOverride = null, long? batchSize = null)
        {
            // local function
            static bool CanEmitAsInputDirectly(SyntaxBase input)
            {
                // the deployment engine only allows JTokenType of String or Object in the copy loop "input" property
                // everything else must be converted into an expression
                return input switch
                {
                    // objects should be emitted as is
                    ObjectSyntax => true,

                    // non-interpolated strings should be emitted as-is
                    StringSyntax @string when !@string.IsInterpolated() => true,

                    // all other expressions should be converted into a language expression before emitting
                    // which will have the resulting JTokenType of String
                    _ => false
                };
            }

            writer.WriteStartObject();

            if (name is not null)
            {
                this.EmitProperty("name", name);
            }

            // construct the length ARM expression from the Bicep array expression
            // type check has already ensured that the array expression is an array
            this.EmitPropertyWithTransform(
                "count",
                syntax.Expression,
                arrayExpression => new FunctionExpression("length", new[] { arrayExpression }, Array.Empty<LanguageExpression>()));

            if (batchSize.HasValue)
            {
                this.EmitProperty("mode", "serial");
                this.EmitProperty("batchSize", () => writer.WriteValue(batchSize.Value));
            }

            if (input != null)
            {
                if (copyIndexOverride == null)
                {
                    if (CanEmitAsInputDirectly(input))
                    {
                        this.EmitProperty("input", input);
                    }
                    else
                    {
                        this.EmitPropertyWithTransform("input", input, converted => ExpressionConverter.ToFunctionExpression(converted));
                    }
                }
                else
                {
                    this.EmitPropertyWithTransform("input", input, expression =>
                    {
                        if (!CanEmitAsInputDirectly(input))
                        {
                            expression = ExpressionConverter.ToFunctionExpression(expression);
                        }

                        // the named copy index in the serialized expression is incorrect
                        // because the object syntax here does not match the JSON equivalent due to the presence of { "value": ... } wrappers
                        // for now, we will manually replace the copy index in the converted expression
                        // this approach will not work for nested property loops
                        var visitor = new LanguageExpressionVisitor
                        {
                            OnFunctionExpression = function =>
                            {
                                if (string.Equals(function.Function, "copyIndex") &&
                                    function.Parameters.Length == 1 &&
                                    function.Parameters[0] is JTokenExpression)
                                {
                                    // it's an invocation of the copyIndex function with 1 argument with a literal value
                                    // replace the argument with the correct value
                                    function.Parameters = new LanguageExpression[] { new JTokenExpression("value") };
                                }
                            }
                        };

                        // mutate the expression
                        expression.Accept(visitor);

                        return expression;
                    });
                }
            }

            writer.WriteEndObject();
        }

        public void EmitObjectProperties(ObjectSyntax objectSyntax, ISet<string>? propertiesToOmit = null)
        {
            var properties = objectSyntax.Properties
                .Where(p => p.TryGetKeyText() is not {} keyName || propertiesToOmit is null || !propertiesToOmit.Contains(keyName))
                .Select(p => new ObjectPropertyOperation(
                    p.TryGetKeyText() is {} keyName ? new ConstantValueOperation(keyName) : GetExpressionOperation(p.Key),
                    GetExpressionOperation(p.Value)));

            var operation = new ObjectOperation(properties.ToImmutableArray());

            EmitObjectProperties(operation);
        }

        private void EmitObjectProperties(ObjectOperation objectOperation)
        {
            var propertyLookup = objectOperation.Properties.ToLookup(property => property.Value is ForLoopOperation);

            // emit loop properties first (if any)
            if (propertyLookup.Contains(true))
            {
                // we have properties whose value is a for-expression
                this.EmitProperty("copy", () =>
                {
                    this.writer.WriteStartArray();

                    foreach (var property in propertyLookup[true])
                    {
                        if (property.Key is not ConstantValueOperation keyValue ||
                            keyValue.Value is not string keyName ||
                            property.Value is not ForLoopOperation forLoop)
                        {
                            // should be caught by loop emit limitation checks
                            throw new InvalidOperationException("Encountered a property with an expression-based key whose value is a for-expression.");
                        }

                        this.EmitCopyObject(keyName, forLoop, forLoop.Body);
                    }

                    this.writer.WriteEndArray();
                });
            }

            // emit non-loop properties
            foreach (var property in propertyLookup[false])
            {
                // property whose value is not a for-expression
                if (property.Key is ConstantValueOperation constantValueOperation &&
                    constantValueOperation.Value is string keyName)
                {
                    EmitProperty(keyName, () => EmitOperation(property.Value));
                }
                else
                {
                    var keyExpression = converter.ConvertOperation(property.Key);
                    EmitPropertyInternal(keyExpression, () => EmitOperation(property.Value));
                }
            }
        }

        public void EmitModuleParameterValue(SyntaxBase syntax)
        {
            if (syntax is InstanceFunctionCallSyntax instanceFunctionCall && string.Equals(instanceFunctionCall.Name.IdentifierName, "getSecret", LanguageConstants.IdentifierComparison))
            {
                var (baseSyntax, indexExpression) = SyntaxHelper.UnwrapArrayAccessSyntax(instanceFunctionCall.BaseExpression);

                if (context.SemanticModel.ResourceMetadata.TryLookup(baseSyntax) is not {} resource ||
                    !StringComparer.OrdinalIgnoreCase.Equals(resource.TypeReference.FormatType(), AzResourceTypeProvider.ResourceTypeKeyVault))
                {
                    throw new InvalidOperationException("Cannot emit parameter's KeyVault secret reference.");
                }

                var keyVaultId = converter.CreateConverterForIndexReplacement(resource.NameSyntax, indexExpression, instanceFunctionCall)
                    .GetFullyQualifiedResourceId(resource);

                writer.WritePropertyName("reference");
                writer.WriteStartObject();
                writer.WritePropertyName("keyVault");
                writer.WriteStartObject();

                writer.WritePropertyName("id");

                var keyVaultIdSerialised = ExpressionSerializer.SerializeExpression(keyVaultId);
                writer.WriteValue(keyVaultIdSerialised);

                writer.WriteEndObject(); // keyVault

                writer.WritePropertyName("secretName");
                var secretName = converter.ConvertExpression(instanceFunctionCall.Arguments[0].Expression);
                var secretNameSerialised = ExpressionSerializer.SerializeExpression(secretName);
                writer.WriteValue(secretNameSerialised);

                if (instanceFunctionCall.Arguments.Length > 1)
                {
                    writer.WritePropertyName("secretVersion");
                    var secretVersion = converter.ConvertExpression(instanceFunctionCall.Arguments[1].Expression);
                    var secretVersionSerialised = ExpressionSerializer.SerializeExpression(secretVersion);
                    writer.WriteValue(secretVersionSerialised);
                }
                writer.WriteEndObject(); // reference

                return;
            }
            EmitProperty("value", syntax);
        }

        public void EmitProperty(string name, LanguageExpression expressionValue)
            => EmitPropertyInternal(new JTokenExpression(name), () =>
            {
                var propertyValue = ExpressionSerializer.SerializeExpression(expressionValue);
                writer.WriteValue(propertyValue);
            });

        public void EmitPropertyWithTransform(string name, Operation value, Func<LanguageExpression, LanguageExpression> convertedValueTransform)
            => EmitPropertyInternal(new JTokenExpression(name), () =>
            {
                var converted = converter.ConvertOperation(value);
                var transformed = convertedValueTransform(converted);
                var serialized = ExpressionSerializer.SerializeExpression(transformed);

                this.writer.WriteValue(serialized);
            });

        public void EmitPropertyWithTransform(string name, SyntaxBase value, Func<LanguageExpression, LanguageExpression> convertedValueTransform)
            => EmitPropertyInternal(new JTokenExpression(name), () =>
            {
                var converted = converter.ConvertExpression(value);
                var transformed = convertedValueTransform(converted);
                var serialized = ExpressionSerializer.SerializeExpression(transformed);

                this.writer.WriteValue(serialized);
            });

        public void EmitProperty(string name, Action valueFunc)
            => EmitPropertyInternal(new JTokenExpression(name), valueFunc);

        public void EmitProperty(string name, string value)
            => EmitPropertyInternal(new JTokenExpression(name), value);

        public void EmitProperty(string name, SyntaxBase expressionValue)
            => EmitPropertyInternal(new JTokenExpression(name), expressionValue);

        public void EmitProperty(SyntaxBase syntaxKey, SyntaxBase syntaxValue)
            => EmitPropertyInternal(converter.ConvertExpression(syntaxKey), syntaxValue);

        private void EmitPropertyInternal(LanguageExpression expressionKey, Action valueFunc)
        {
            var serializedName = ExpressionSerializer.SerializeExpression(expressionKey);
            writer.WritePropertyName(serializedName);

            valueFunc();
        }

        private void EmitPropertyInternal(LanguageExpression expressionKey, string value)
            => EmitPropertyInternal(expressionKey, () =>
            {
                var propertyValue = ExpressionSerializer.SerializeExpression(new JTokenExpression(value));
                writer.WriteValue(propertyValue);
            });

        private void EmitPropertyInternal(LanguageExpression expressionKey, SyntaxBase syntaxValue)
            => EmitPropertyInternal(expressionKey, () => EmitExpression(syntaxValue));

        public void EmitOptionalPropertyExpression(string name, SyntaxBase? expression)
        {
            if (expression != null)
            {
                EmitProperty(name, expression);
            }
        }
    }
}

