﻿namespace System
open System.Reflection

[<assembly: AssemblyTitleAttribute("FsPickler")>]
[<assembly: AssemblyProductAttribute("FsPickler")>]
[<assembly: AssemblyCopyrightAttribute("© Eirik Tsarpalis.")>]
[<assembly: AssemblyVersionAttribute("1.2.19")>]
[<assembly: AssemblyFileVersionAttribute("1.2.19")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "1.2.19"