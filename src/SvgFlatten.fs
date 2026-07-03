module SvgExtrude.SvgFlatten

open System
open Fable.Core
open Fable.Core.JsInterop
open Browser.Types
open SvgExtrude.Types
open SvgExtrude.PathParser

[<Emit("parseFloat($0)")>]
let private parseFloatJs (s: string) : float = jsNative

/// 2D affine matrix, same layout as SVGMatrix (a b c d e f).
type Matrix = { A: float; B: float; C: float; D: float; E: float; F: float }

let identity = { A = 1.0; B = 0.0; C = 0.0; D = 1.0; E = 0.0; F = 0.0 }

/// m * n: apply n first, then m.
let mul (m: Matrix) (n: Matrix) =
    { A = m.A * n.A + m.C * n.B
      B = m.B * n.A + m.D * n.B
      C = m.A * n.C + m.C * n.D
      D = m.B * n.C + m.D * n.D
      E = m.A * n.E + m.C * n.F + m.E
      F = m.B * n.E + m.D * n.F + m.F }

let apply (m: Matrix) (p: Pt) =
    { X = m.A * p.X + m.C * p.Y + m.E
      Y = m.B * p.X + m.D * p.Y + m.F }

/// Matrix from the element's own `transform` attribute (identity if none).
let private localMatrix (el: Element) : Matrix =
    let tl: obj = el?transform
    if isNull tl then identity
    else
        let bv: obj = tl?baseVal
        if isNull bv then identity
        else
            let cnt: int = bv?numberOfItems
            let mutable m = identity
            for k in 0 .. cnt - 1 do
                let mk: obj = (bv?getItem (k))?matrix
                m <- mul m { A = mk?a; B = mk?b; C = mk?c; D = mk?d; E = mk?e; F = mk?f }
            m

/// Accumulated transform from the element up to (excluding) the root svg,
/// i.e. into the root's viewBox user-unit space.
let matrixToRoot (root: Element) (el: Element) : Matrix =
    let mutable m = identity
    let mutable e = el
    while not (isNull e) && not (obj.ReferenceEquals(e, root)) do
        m <- mul (localMatrix e) m
        e <- e.parentElement :> Element
    m

let private attrF (el: Element) (name: string) (dflt: float) =
    let v = el.getAttribute name
    if isNull v || v = "" then dflt
    else
        let f = parseFloatJs v
        if Double.IsNaN f then dflt else f

/// Sampled full ellipse ring, chord error <= tol.
let private ellipseRing tol cx cy rx ry : Pt array =
    let step = maxAngleStep tol (max rx ry)
    let n = max 12 (int (ceil (2.0 * Math.PI / step)))
    [| for k in 0 .. n - 1 ->
         let th = 2.0 * Math.PI * float k / float n
         { X = cx + rx * cos th; Y = cy + ry * sin th } |]

let private rectSubpaths tol (el: Element) : Subpath list =
    let x = attrF el "x" 0.0
    let y = attrF el "y" 0.0
    let w = attrF el "width" 0.0
    let h = attrF el "height" 0.0
    if w <= 0.0 || h <= 0.0 then []
    else
        // SVG auto rules: a missing corner radius borrows the other one.
        let rxRaw = attrF el "rx" nan
        let ryRaw = attrF el "ry" nan
        let rx0 = if Double.IsNaN rxRaw then (if Double.IsNaN ryRaw then 0.0 else ryRaw) else rxRaw
        let ry0 = if Double.IsNaN ryRaw then rx0 else ryRaw
        let rx = min (max 0.0 rx0) (w / 2.0)
        let ry = min (max 0.0 ry0) (h / 2.0)
        if rx <= 0.0 || ry <= 0.0 then
            [ { Points =
                  [| { X = x; Y = y }
                     { X = x + w; Y = y }
                     { X = x + w; Y = y + h }
                     { X = x; Y = y + h } |]
                Closed = true } ]
        else
            let step = maxAngleStep tol (max rx ry)
            let segs = max 2 (int (ceil (Math.PI / 2.0 / step)))
            let pts = ResizeArray<Pt>()
            // Quarter ellipse centered at (cx, cy) from angle a0 to a0 + 90deg.
            let corner cx cy a0 =
                for k in 0 .. segs do
                    let th = a0 + (Math.PI / 2.0) * float k / float segs
                    pts.Add { X = cx + rx * cos th; Y = cy + ry * sin th }
            corner (x + w - rx) (y + ry) (-Math.PI / 2.0)      // top-right
            corner (x + w - rx) (y + h - ry) 0.0               // bottom-right
            corner (x + rx) (y + h - ry) (Math.PI / 2.0)       // bottom-left
            corner (x + rx) (y + ry) Math.PI                   // top-left
            [ { Points = pts.ToArray(); Closed = true } ]

let private parsePointsAttr (s: string) : Pt array =
    if isNull s then [||]
    else
        let nums =
            s.Split([| ' '; '\t'; '\n'; '\r'; ',' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map parseFloatJs
            |> Array.filter (fun f -> not (Double.IsNaN f))
        [| for k in 0 .. nums.Length / 2 - 1 -> { X = nums.[2 * k]; Y = nums.[2 * k + 1] } |]

/// Flatten one drawable element into subpaths in its own local coordinates.
let flattenElement (tol: float) (el: Element) : Subpath list =
    match el.tagName.ToLower() with
    | "path" ->
        let d = el.getAttribute "d"
        if isNull d then [] else parse tol d
    | "rect" -> rectSubpaths tol el
    | "circle" ->
        let r = attrF el "r" 0.0
        if r <= 0.0 then []
        else [ { Points = ellipseRing tol (attrF el "cx" 0.0) (attrF el "cy" 0.0) r r; Closed = true } ]
    | "ellipse" ->
        let rx = attrF el "rx" 0.0
        let ry = attrF el "ry" 0.0
        if rx <= 0.0 || ry <= 0.0 then []
        else [ { Points = ellipseRing tol (attrF el "cx" 0.0) (attrF el "cy" 0.0) rx ry; Closed = true } ]
    | "polygon" ->
        let pts = parsePointsAttr (el.getAttribute "points")
        if pts.Length < 3 then [] else [ { Points = pts; Closed = true } ]
    | "polyline" ->
        let pts = parsePointsAttr (el.getAttribute "points")
        if pts.Length < 2 then [] else [ { Points = pts; Closed = false } ]
    | _ -> []

/// Flatten an element and classify its rings into shapes, transformed into the
/// root svg's viewBox space.
let parseElement (root: Element) (tol: float) (id: string) (label: string) (el: Element) : ParsedElement =
    let m = matrixToRoot root el
    let subs =
        flattenElement tol el
        |> List.map (fun s -> { s with Points = s.Points |> Array.map (apply m) })
    let shapes, openCount = Rings.toShapes subs
    { Id = id; Label = label; Shapes = shapes; OpenSubpaths = openCount }
