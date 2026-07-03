module SvgExtrude.Stl

open Fable.Core
open Browser.Dom

[<Emit("new ArrayBuffer($0)")>]
let private newArrayBuffer (size: int) : obj = jsNative

[<Emit("new DataView($0)")>]
let private newDataView (buf: obj) : obj = jsNative

[<Emit("$0.setUint8($1, $2)")>]
let private setU8 (dv: obj) (off: int) (v: int) : unit = jsNative

[<Emit("$0.setUint32($1, $2, true)")>]
let private setU32 (dv: obj) (off: int) (v: float) : unit = jsNative

[<Emit("$0.setFloat32($1, $2, true)")>]
let private setF32 (dv: obj) (off: int) (v: float) : unit = jsNative

[<Emit("new Blob([$0], { type: 'model/stl' })")>]
let private newBlob (buf: obj) : obj = jsNative

[<Emit("URL.createObjectURL($0)")>]
let private createObjectURL (b: obj) : string = jsNative

[<Emit("URL.revokeObjectURL($0)")>]
let private revokeObjectURL (u: string) : unit = jsNative

/// Build a binary STL from concatenated triangle-position lists
/// (9 floats per triangle, as produced by Geometry.extrude).
let build (parts: float array list) : obj =
    let totalTris = parts |> List.sumBy (fun p -> p.Length / 9)
    let buf = newArrayBuffer (84 + 50 * totalTris)
    let dv = newDataView buf
    let header = "SVG Extrude STL - binary (color is preview-only)"
    for k in 0 .. min 79 (header.Length - 1) do
        setU8 dv k (int header.[k])
    setU32 dv 80 (float totalTris)
    let mutable off = 84
    for part in parts do
        for t in 0 .. part.Length / 9 - 1 do
            let i = t * 9
            let ax, ay, az = part.[i], part.[i + 1], part.[i + 2]
            let bx, by, bz = part.[i + 3], part.[i + 4], part.[i + 5]
            let cx, cy, cz = part.[i + 6], part.[i + 7], part.[i + 8]
            let ux, uy, uz = bx - ax, by - ay, bz - az
            let vx, vy, vz = cx - ax, cy - ay, cz - az
            let mutable nx = uy * vz - uz * vy
            let mutable ny = uz * vx - ux * vz
            let mutable nz = ux * vy - uy * vx
            let len = sqrt (nx * nx + ny * ny + nz * nz)
            if len > 1e-30 then
                nx <- nx / len
                ny <- ny / len
                nz <- nz / len
            else
                nx <- 0.0
                ny <- 0.0
                nz <- 0.0
            setF32 dv off nx
            setF32 dv (off + 4) ny
            setF32 dv (off + 8) nz
            setF32 dv (off + 12) ax
            setF32 dv (off + 16) ay
            setF32 dv (off + 20) az
            setF32 dv (off + 24) bx
            setF32 dv (off + 28) by
            setF32 dv (off + 32) bz
            setF32 dv (off + 36) cx
            setF32 dv (off + 40) cy
            setF32 dv (off + 44) cz
            // 2 attribute bytes stay zero (ArrayBuffer is zero-initialized).
            off <- off + 50
    buf

/// Trigger a browser download of the buffer as an STL file.
let download (filename: string) (buf: obj) : unit =
    let url = createObjectURL (newBlob buf)
    let a = document.createElement "a" :?> Browser.Types.HTMLAnchorElement
    a.href <- url
    a.setAttribute ("download", filename)
    document.body.appendChild a |> ignore
    a.click ()
    document.body.removeChild (a :> Browser.Types.Node) |> ignore
    revokeObjectURL url
