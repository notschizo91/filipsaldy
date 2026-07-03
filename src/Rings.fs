module SvgExtrude.Rings

open SvgExtrude.Types
open SvgExtrude.PathParser

/// Shoelace signed area. Positive = counter-clockwise in a y-up space
/// (clockwise in SVG's y-down space).
let signedArea (ring: Ring) =
    let mutable s = 0.0
    for k in 0 .. ring.Length - 1 do
        let a = ring.[k]
        let b = ring.[(k + 1) % ring.Length]
        s <- s + (a.X * b.Y - b.X * a.Y)
    s / 2.0

/// Even-odd raycast point-in-polygon test.
let contains (ring: Ring) (p: Pt) =
    let mutable inside = false
    let mutable j = ring.Length - 1
    for k in 0 .. ring.Length - 1 do
        let a = ring.[k]
        let b = ring.[j]
        if (a.Y > p.Y) <> (b.Y > p.Y) then
            let xInt = a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X)
            if p.X < xInt then inside <- not inside
        j <- k
    inside

let private dist2 (a: Pt) (b: Pt) =
    let dx = b.X - a.X
    let dy = b.Y - a.Y
    dx * dx + dy * dy

/// Remove consecutive duplicate points and a duplicated closing point.
let private cleanRing (pts: Pt array) : Ring =
    let out = ResizeArray<Pt>()
    for p in pts do
        if out.Count = 0 || dist2 out.[out.Count - 1] p > 1e-18 then out.Add p
    if out.Count > 1 && dist2 out.[0] out.[out.Count - 1] <= 1e-18 then
        out.RemoveAt(out.Count - 1)
    out.ToArray()

/// Classify one element's flattened subpaths into fillable shapes.
/// Rings nested at odd depth become holes of their innermost containing outer
/// ring (even-odd fill semantics). Open subpaths are auto-closed and counted
/// so the UI can warn about them.
let toShapes (subs: Subpath list) : Shape list * int =
    let openCount =
        subs |> List.filter (fun s -> not s.Closed && s.Points.Length > 1) |> List.length
    let rings =
        subs
        |> List.map (fun s -> cleanRing s.Points)
        |> List.filter (fun r -> r.Length >= 3 && abs (signedArea r) > 1e-9)
        |> List.toArray
    if rings.Length = 0 then
        [], openCount
    else
        let depth =
            rings
            |> Array.mapi (fun i r ->
                let p = r.[0]
                rings
                |> Array.mapi (fun j rj -> if j <> i && contains rj p then 1 else 0)
                |> Array.sum)
        let holesFor = System.Collections.Generic.Dictionary<int, ResizeArray<Ring>>()
        for k in 0 .. rings.Length - 1 do
            if depth.[k] % 2 = 0 then holesFor.[k] <- ResizeArray()
        for k in 0 .. rings.Length - 1 do
            if depth.[k] % 2 = 1 then
                // Attach the hole to the smallest outer ring that contains it.
                let p = rings.[k].[0]
                let mutable best = -1
                let mutable bestArea = infinity
                for j in 0 .. rings.Length - 1 do
                    if j <> k && depth.[j] % 2 = 0 && contains rings.[j] p then
                        let a = abs (signedArea rings.[j])
                        if a < bestArea then
                            bestArea <- a
                            best <- j
                if best >= 0 then holesFor.[best].Add rings.[k]
        let shapes =
            [ for kv in holesFor -> { Outer = rings.[kv.Key]; Holes = List.ofSeq kv.Value } ]
        shapes, openCount
