module SvgExtrude.Geometry

open Fable.Core
open SvgExtrude.Types
open SvgExtrude.Rings

[<ImportDefault("earcut")>]
let private earcut (data: float array) (holeIndices: int array) (dim: int) : int array = jsNative

let private ensureWinding ccw (ring: Ring) : Ring =
    if (signedArea ring > 0.0) <> ccw then Array.rev ring else ring

/// Extrude one shape (2D coords in mm, y-up) from z = 0 to z = height.
///
/// Pure function of (shape, height); used identically by the live preview and
/// the STL export. Returns non-indexed triangle positions (9 floats per
/// triangle, consistently outward-facing winding) plus the number of
/// degenerate triangles that were dropped.
let extrude (shape: Shape) (height: float) : float array * int =
    let outer = ensureWinding true shape.Outer
    let holes = shape.Holes |> List.map (ensureWinding false)
    let rings = outer :: holes

    // Flat coordinate list + hole start indices, as earcut expects.
    let totalPts = rings |> List.sumBy Array.length
    let coords = Array.zeroCreate<float> (totalPts * 2)
    let allPts = Array.zeroCreate<Pt> totalPts
    let holeIdx = ResizeArray<int>()
    let mutable w = 0
    for ring in rings do
        if w > 0 then holeIdx.Add w
        for p in ring do
            allPts.[w] <- p
            coords.[2 * w] <- p.X
            coords.[2 * w + 1] <- p.Y
            w <- w + 1

    let idx = earcut coords (holeIdx.ToArray()) 2

    let out = ResizeArray<float>()
    let mutable degenerate = 0
    let addTri ax ay az bx by bz cx cy cz =
        let ux, uy, uz = bx - ax, by - ay, bz - az
        let vx, vy, vz = cx - ax, cy - ay, cz - az
        let nx = uy * vz - uz * vy
        let ny = uz * vx - ux * vz
        let nz = ux * vy - uy * vx
        if nx * nx + ny * ny + nz * nz < 1e-18 then
            degenerate <- degenerate + 1
        else
            out.Add ax; out.Add ay; out.Add az
            out.Add bx; out.Add by; out.Add bz
            out.Add cx; out.Add cy; out.Add cz

    // Caps. earcut's output winding follows the input rings, so normalize each
    // triangle to CCW before emitting: top cap faces +Z, mirrored bottom cap -Z.
    for t in 0 .. idx.Length / 3 - 1 do
        let a = allPts.[idx.[3 * t]]
        let b = allPts.[idx.[3 * t + 1]]
        let c = allPts.[idx.[3 * t + 2]]
        let cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)
        let b, c = if cross < 0.0 then c, b else b, c
        addTri a.X a.Y height b.X b.Y height c.X c.Y height
        addTri a.X a.Y 0.0 c.X c.Y 0.0 b.X b.Y 0.0

    // Walls: one quad (two triangles) per boundary edge, outer ring and holes
    // alike. With outer CCW and holes CW the same construction faces outward.
    for ring in rings do
        for k in 0 .. ring.Length - 1 do
            let p = ring.[k]
            let q = ring.[(k + 1) % ring.Length]
            addTri p.X p.Y 0.0 q.X q.Y 0.0 q.X q.Y height
            addTri p.X p.Y 0.0 q.X q.Y height p.X p.Y height

    out.ToArray(), degenerate

/// Axis-aligned bounds of a set of shapes (outer rings only).
let bounds (shapes: Shape seq) : (float * float * float * float) option =
    let mutable minX = infinity
    let mutable minY = infinity
    let mutable maxX = -infinity
    let mutable maxY = -infinity
    let mutable any = false
    for s in shapes do
        for p in s.Outer do
            any <- true
            if p.X < minX then minX <- p.X
            if p.Y < minY then minY <- p.Y
            if p.X > maxX then maxX <- p.X
            if p.Y > maxY then maxY <- p.Y
    if any then Some(minX, minY, maxX, maxY) else None

/// Map a shape through a point transform (e.g. SVG units -> centered mm, y-up).
let mapShape (f: Pt -> Pt) (s: Shape) : Shape =
    { Outer = Array.map f s.Outer
      Holes = List.map (Array.map f) s.Holes }
