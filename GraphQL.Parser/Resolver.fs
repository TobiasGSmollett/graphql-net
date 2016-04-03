﻿//MIT License
//
//Copyright (c) 2016 Robert Peele
//
//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:
//
//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.
//
//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.

namespace GraphQL.Parser.SchemaAST
open GraphQL.Parser
open System
open System.Collections.Generic

type ValidationException(msg : string, pos : SourceInfo) =
    inherit Exception(msg)
    member this.SourceInfo = pos

/// Resolves variables and fragments in the context of a specific operation.
type IOperationContext<'s> =
    abstract member Schema : ISchema<'s>
    /// Add a variable definition to the context.
    abstract member DeclareVariable : string * QLType<'s> * Value<'s> option -> VariableDefinition<'s>
    abstract member ResolveVariableByName : string -> VariableDefinition<'s> option
    abstract member ResolveFragmentDefinitionByName : string -> ParserAST.Fragment option

module private ResolverUtilities =
    let failAt pos msg =
        new ValidationException(msg, pos) |> raise
    type IOperationContext<'s> with
        member this.ResolveValue(pvalue : ParserAST.Value, pos : SourceInfo) : Value<'s> =
            match pvalue with
            | ParserAST.Variable name ->
                match this.ResolveVariableByName name with
                | Some vdef -> VariableRefValue vdef
                | None -> failAt pos (sprintf "use of undeclared variable ``%s''" name)
            | ParserAST.IntValue i ->
                PrimitiveValue (IntPrimitive i)
            | ParserAST.FloatValue f ->
                PrimitiveValue (FloatPrimitive f)
            | ParserAST.StringValue s ->
                PrimitiveValue (StringPrimitive s)
            | ParserAST.BooleanValue b ->
                PrimitiveValue (BooleanPrimitive b)
            | ParserAST.EnumValue enumName ->
                match this.Schema.ResolveEnumValueByName enumName with
                | None -> failAt pos (sprintf "``%s'' is not a member of any known enum type" enumName)
                | Some enumVal -> EnumValue enumVal
            | ParserAST.ListValue elementsWithSource ->
                [|
                    for element in elementsWithSource do
                        let vvalue = this.ResolveValue(element.Value, element.Source)
                        yield { Value = vvalue; Source = element.Source }
                |] :> IReadOnlyList<_> |> ListValue
            | ParserAST.ObjectValue fieldsWithSource ->
                seq {
                    for KeyValue(fieldName, fieldVal) in fieldsWithSource do
                        let vvalue = this.ResolveValue(fieldVal.Value, fieldVal.Source)
                        yield fieldName, { Value = vvalue; Source = fieldVal.Source }
                } |> dictionary :> IReadOnlyDictionary<_, _> |> ObjectValue
    type ISchema<'s> with
        member this.ResolveType(ptype : ParserAST.TypeDescription, pos : SourceInfo) : QLType<'s> =
            let coreTy =
                match ptype.Type with
                | ParserAST.NamedType name ->
                    match this.ResolveTypeByName(name) with
                    | None -> failAt pos (sprintf "unknown type ``%s''" name)
                    | Some schemaTy -> NamedType schemaTy
                | ParserAST.ListType ptys ->
                    [|
                        for { Source = pos; Value = pty } in ptys do
                            yield { Source = pos; Value = this.ResolveType(pty, pos) }
                    |] :> IReadOnlyList<_> |> ListType
            { Type = coreTy; Nullable = ptype.Nullable }
open ResolverUtilities

type Resolver<'s>
    ( schemaType : ISchemaType<'s> // the type being selected from
    , opContext : IOperationContext<'s>
    , recursionDepth : int
    ) =
    static let maxRecursionDepth = 10 // should be plenty for real queries
    member private this.ResolveArguments
        ( schemaArgs : IReadOnlyDictionary<string, ISchemaArgument<'s>>
        , pargs : ParserAST.Argument WithSource seq
        ) =
        [|
            for { Source = pos; Value = parg } in pargs do
                match schemaArgs.TryFind(parg.ArgumentName) with
                | None -> failAt pos (sprintf "unknown argument ``%s''" parg.ArgumentName)
                | Some arg ->
                    let pargValue = opContext.ResolveValue(parg.ArgumentValue, pos)
                    match arg.ValidateValue(pargValue) with
                    | Invalid reason ->
                        failAt pos (sprintf "invalid argument ``%s'': ``%s''" parg.ArgumentName reason)
                    | Valid argVal ->
                        yield { Value = argVal; Source = pos }
        |] :> IReadOnlyList<_>
    member private this.ResolveDirectives(pdirs : ParserAST.Directive WithSource seq) =
        [|
            for { Source = pos; Value = pdir } in pdirs do
                match opContext.Schema.ResolveDirectiveByName(pdir.DirectiveName) with
                | None -> failAt pos (sprintf "unknown directive ``%s''" pdir.DirectiveName)
                | Some dir ->
                    let args = this.ResolveArguments(dir.Arguments, pdir.Arguments)
                    yield {
                        Value = { SchemaDirective = dir; Arguments = args }
                        Source = pos
                    }
        |] :> IReadOnlyList<_>
    member private this.ResolveFieldSelection(pfield : ParserAST.Field, pos : SourceInfo) =
        match schemaType.Fields.TryFind(pfield.FieldName) with
        | None -> failAt pos (sprintf "``%s'' is not a field of type ``%s''" pfield.FieldName schemaType.TypeName)
        | Some fieldInfo ->
            let directives = this.ResolveDirectives(pfield.Directives)
            let arguments = this.ResolveArguments(fieldInfo.Arguments, pfield.Arguments)
            let selections =
                if pfield.Selections.Count <= 0 then
                    [||] :> IReadOnlyList<_>
                else
                    if recursionDepth >= maxRecursionDepth then
                        failAt
                            pfield.Selections.[0].Source
                            (sprintf "exceeded maximum recursion depth of %d" maxRecursionDepth)
                    let child = new Resolver<'s>(fieldInfo.FieldType, opContext, recursionDepth + 1)
                    child.ResolveSelections(pfield.Selections)
            {
                SchemaField = fieldInfo
                Alias = pfield.Alias
                Directives = directives
                Arguments = arguments
                Selections = selections
            }
    member private this.ResolveTypeCondition(typeName : string, pos : SourceInfo) =
        match opContext.Schema.ResolveTypeByName(typeName) with
        | None -> failAt pos (sprintf "unknown type ``%s'' in type condition" typeName)
        | Some ty -> ty
    member private this.ResolveFragment(pfrag : ParserAST.Fragment, pos : SourceInfo) =
        let directives = this.ResolveDirectives(pfrag.Directives)
        let selections = this.ResolveSelections(pfrag.Selections)
        let typeCondition = this.ResolveTypeCondition(pfrag.TypeCondition, pos)
        {
            FragmentName = pfrag.FragmentName
            TypeCondition = typeCondition
            Directives = directives
            Selections = selections
        }
    member private this.ResolveFragmentSpreadSelection
        (pspread : ParserAST.FragmentSpread, pos : SourceInfo) =
        match opContext.ResolveFragmentDefinitionByName(pspread.FragmentName) with
        | None -> failAt pos (sprintf "unknown fragment ``%s''" pspread.FragmentName)
        | Some pfrag ->
            let frag = this.ResolveFragment(pfrag, pos)
            {
                Fragment = frag
                Directives = this.ResolveDirectives(pspread.Directives)
            }
    member private this.ResolveInlineFragment
        (pinline : ParserAST.InlineFragment, pos : SourceInfo) =
        let directives = this.ResolveDirectives(pinline.Directives)
        let selections = this.ResolveSelections(pinline.Selections)
        let typeCondition =
            match pinline.TypeCondition with
            | None -> None
            | Some typeName -> Some <| this.ResolveTypeCondition(typeName, pos)
        {
            TypeCondition = typeCondition
            Directives = directives
            Selections = selections
        }
    member private this.ResolveSelection(pselection : ParserAST.Selection, pos : SourceInfo) =
        match pselection with
        | ParserAST.FieldSelection pfield ->
            this.ResolveFieldSelection(pfield, pos)
            |> FieldSelection
        | ParserAST.FragmentSpreadSelection pfragmentSpread ->
            this.ResolveFragmentSpreadSelection(pfragmentSpread, pos)
            |> FragmentSpreadSelection
        | ParserAST.InlineFragmentSelection pinlineFragment ->
            this.ResolveInlineFragment(pinlineFragment, pos)
            |> InlineFragmentSelection
    member this.ResolveSelections(pselections : ParserAST.Selection WithSource seq) =
        [|
            for { Source = pos; Value = pselection } in pselections do
                yield { Source = pos; Value = this.ResolveSelection(pselection, pos) }
        |] :> IReadOnlyList<_>
    member this.ResolveOperation(poperation : ParserAST.Operation, pos : SourceInfo) =
        match poperation with
        | ParserAST.ShorthandOperation pselections ->
            let selections = this.ResolveSelections(pselections)
            ShorthandOperation selections
        | ParserAST.LonghandOperation plonghand ->
            let variableDefinitions =
                [|
                    for { Source = pos; Value = pvarDef } in plonghand.VariableDefinitions do
                        match opContext.ResolveVariableByName(pvarDef.VariableName) with
                        | None -> () // good, we're declaring a new variable
                        | Some _ ->
                            failAt pos (sprintf "duplicate declaration of variable ``%s''" pvarDef.VariableName)
                        let varType = opContext.Schema.ResolveType(pvarDef.Type, pos)
                        let defaultValue =
                            pvarDef.DefaultValue
                            |> Option.map (fun v -> opContext.ResolveValue(v, pos))
                        let varDef = opContext.DeclareVariable(pvarDef.VariableName, varType, defaultValue)
                        yield { Source = pos; Value = varDef }
                |]
            {
                OperationType =
                    match plonghand.Type with
                    | ParserAST.Mutation -> Mutation
                    | ParserAST.Query -> Query
                OperationName = plonghand.Name
                VariableDefinitions = variableDefinitions
                Directives = this.ResolveDirectives(plonghand.Directives)
                Selections = this.ResolveSelections(plonghand.Selections)
            } |> LonghandOperation

type DocumentContext<'s>(schema : ISchema<'s>, document : ParserAST.Document) =
    let fragments = new Dictionary<string, ParserAST.Fragment>()
    do
        for { Source = pos; Value = definition } in document.Definitions do
            match definition with
            | ParserAST.FragmentDefinition frag ->
                if fragments.ContainsKey(frag.FragmentName) then
                    failAt pos (sprintf "duplicate definition of fragment ``%s''" frag.FragmentName)
                else
                    fragments.Add(frag.FragmentName, frag)
            | ParserAST.OperationDefinition _ -> () // ignore operations
    member __.ResolveFragmentDefinitionByName(name) =
        fragments.TryFind(name)
    member this.ResolveOperations() =
        [|
            for { Source = pos; Value = definition } in document.Definitions do
                match definition with
                | ParserAST.OperationDefinition operation ->
                    let opContext = new OperationContext<'s>(schema, this)
                    let resolver = new Resolver<'s>(schema.RootType, opContext, 0)
                    let op = resolver.ResolveOperation(operation, pos)
                    yield { Source = pos; Value = op }
                | ParserAST.FragmentDefinition _ -> () // ignore fragments
        |]
/// Resolves variables and fragments in the context of a specific operation.
and OperationContext<'s>(schema : ISchema<'s>, document : DocumentContext<'s>) =
    let variableDefs = new Dictionary<string, VariableDefinition<'s>>()
    interface IOperationContext<'s> with
        member __.Schema = schema
        member __.DeclareVariable(name, qlType, defaultValue) =
            let def = { VariableName = name; VariableType = qlType; DefaultValue= defaultValue }
            variableDefs.Add(name, def)
            def
        member __.ResolveVariableByName(name) =
            variableDefs.TryFind(name)
        member __.ResolveFragmentDefinitionByName(name) =
            document.ResolveFragmentDefinitionByName(name)