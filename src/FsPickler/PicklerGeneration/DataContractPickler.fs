﻿namespace Nessos.FsPickler

    open System
    open System.IO
    open System.Reflection
    open System.Threading
    open System.Runtime.Serialization

    open Nessos.FsPickler
    open Nessos.FsPickler.Reflection

#if EMIT_IL
    open System.Reflection.Emit
    open Nessos.FsPickler.Emit
    open Nessos.FsPickler.PicklerEmit
#endif

    type internal DataContractPickler =

        static member Create<'T>(resolver : IPicklerResolver) =

            let t = typeof<'T>
            let cacheByRef = not <| t.IsValueType
            let properties = gatherMembers t |> Array.choose (function :? PropertyInfo as p -> Some p | _ -> None)
            // check if data members are to be resolved by member exclusion
            let isExclusive = properties |> Array.exists (Option.isSome << tryGetAttr<IgnoreDataMemberAttribute>)
            
            let tryGetDataMemberInfo (p : PropertyInfo) =
                // resolve if property is data member
                let dataAttr =
                    match tryGetAttr<DataMemberAttribute> p with
                    | Some _ as attr -> attr
                    | None when isExclusive ->
                        // check for ignored data member attributes
                        if Option.isSome <| tryGetAttr<IgnoreDataMemberAttribute> p then
                            None
                        else
                            // not ignored, annotate with trivial data member attribute.
                            Some(new DataMemberAttribute())
                    | None -> None

                match dataAttr with
                | None -> None
                | Some _ when not p.CanRead ->
                    let msg = sprintf "property '%s' marked as data member but missing getter." p.Name
                    raise <| new PicklerGenerationException(t, msg)

                | Some _ when not p.CanWrite ->
                    let msg = sprintf "property '%s' marked as data member but missing setter." p.Name
                    raise <| new PicklerGenerationException(t, msg)

                | Some attr -> Some(attr, p)

            let dataContractInfo = 
                properties
                |> Seq.choose tryGetDataMemberInfo
                |> Seq.mapi (fun i v -> (i,v))
                // sort data members: primarily specified by user-specified order
                // and secondarily by definition order
                |> Seq.sortBy (fun (i,(attr,_)) -> (attr.Order, i))
                |> Seq.map snd
                |> Seq.toArray

            let properties = dataContractInfo |> Array.map snd
            let picklers = properties |> Array.map (fun p -> resolver.Resolve p.PropertyType)
            let names = dataContractInfo |> Array.mapi (fun i (attr,p) -> match attr.Name with null | "" -> getNormalizedFieldName i p.Name | name -> getNormalizedFieldName i name)

            let isDeserializationCallback = isAssignableFrom typeof<IDeserializationCallback> typeof<'T>

            let allMethods = typeof<'T>.GetMethods(allMembers)
            let onSerializing = allMethods |> getSerializationMethods<OnSerializingAttribute>
            let onSerialized = allMethods |> getSerializationMethods<OnSerializedAttribute>
            let onDeserializing = allMethods |> getSerializationMethods<OnDeserializingAttribute>
            let onDeserialized = allMethods |> getSerializationMethods<OnDeserializedAttribute>

#if EMIT_IL
            let writerDele =
                DynamicMethod.compileAction3<Pickler [], WriteState, 'T> "dataContractSerializer" (fun picklers writer parent ilGen ->

                    emitSerializationMethodCalls onSerializing (Choice1Of2 writer) parent ilGen

                    emitSerializeProperties properties names writer picklers parent ilGen

                    emitSerializationMethodCalls onSerialized (Choice1Of2 writer) parent ilGen

                    ilGen.Emit OpCodes.Ret)

            let readerDele =
                DynamicMethod.compileFunc2<Pickler [], ReadState, 'T> "dataContractDeserializer" (fun picklers reader ilGen ->
                    // initialize empty value type
                    let value = EnvItem<'T>(ilGen)
                    emitObjectInitializer typeof<'T> ilGen
                    value.Store ()

                    emitSerializationMethodCalls onDeserializing (Choice2Of2 reader) value ilGen

                    emitDeserializeProperties properties names reader picklers value ilGen

                    emitSerializationMethodCalls onDeserialized (Choice2Of2 reader) value ilGen

                    if isDeserializationCallback then emitDeserializationCallback value ilGen

                    value.Load ()
                    ilGen.Emit OpCodes.Ret
                )

            let writer w t v = writerDele.Invoke(picklers, w, v)
            let reader r t = readerDele.Invoke(picklers, r)
                
#else
            let inline run (ms : MethodInfo []) (x : obj) w =
                for i = 0 to ms.Length - 1 do 
                    ms.[i].Invoke(x, [| getStreamingContext w :> obj |]) |> ignore

            let writer (w : WriteState) (tag : string) (t : 'T) =
                run onSerializing t w

                for i = 0 to properties.Length - 1 do
                    let value = properties.[i].GetValue t
                    picklers.[i].UntypedWrite w names.[i] value

                run onSerialized t w

            let reader (r : ReadState) (tag : string) =
                let t = FormatterServices.GetUninitializedObject(t) |> fastUnbox<'T>
                run onDeserializing t r

                for i = 0 to properties.Length - 1 do
                    let value = picklers.[i].UntypedRead r names.[i]
                    properties.[i].SetValue(t, value)

                run onDeserialized t r
                if isDeserializationCallback then (fastUnbox<IDeserializationCallback> t).OnDeserialization null
                t
#endif

            CompositePickler.Create(reader, writer, PicklerInfo.DataContract, cacheByRef = cacheByRef, useWithSubtypes = false)