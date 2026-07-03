module SvgExtrude.PathParser

open System
open SvgExtrude.Types

/// One flattened subpath: polyline points in draw order, plus whether the
/// source subpath was explicitly closed with Z.
type Subpath = { Points: Pt array; Closed: bool }

let private dist (a: Pt) (b: Pt) =
    let dx = b.X - a.X
    let dy = b.Y - a.Y
    sqrt (dx * dx + dy * dy)

/// Distance from point p to segment a-b.
let private distToSeg (p: Pt) (a: Pt) (b: Pt) =
    let dx = b.X - a.X
    let dy = b.Y - a.Y
    let len2 = dx * dx + dy * dy
    if len2 < 1e-12 then
        dist p a
    else
        let t = max 0.0 (min 1.0 (((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2))
        dist p { X = a.X + t * dx; Y = a.Y + t * dy }

let private lerp (a: Pt) (b: Pt) t =
    { X = a.X + (b.X - a.X) * t; Y = a.Y + (b.Y - a.Y) * t }

/// Adaptive cubic bezier flattening: subdivide until control points sit within
/// `tol` of the chord, then emit the endpoint.
let rec private flattenCubic tol depth (p0: Pt) (p1: Pt) (p2: Pt) (p3: Pt) (acc: ResizeArray<Pt>) =
    if depth >= 18 || (distToSeg p1 p0 p3 <= tol && distToSeg p2 p0 p3 <= tol) then
        acc.Add p3
    else
        let p01 = lerp p0 p1 0.5
        let p12 = lerp p1 p2 0.5
        let p23 = lerp p2 p3 0.5
        let p012 = lerp p01 p12 0.5
        let p123 = lerp p12 p23 0.5
        let mid = lerp p012 p123 0.5
        flattenCubic tol (depth + 1) p0 p01 p012 mid acc
        flattenCubic tol (depth + 1) mid p123 p23 p3 acc

/// Quadratic bezier via exact degree elevation to cubic.
let private flattenQuad tol (p0: Pt) (q: Pt) (p3: Pt) (acc: ResizeArray<Pt>) =
    let c1 = lerp p0 q (2.0 / 3.0)
    let c2 = lerp p3 q (2.0 / 3.0)
    flattenCubic tol 0 p0 c1 c2 p3 acc

/// Number of samples so the chord error of a circular arc of radius r stays under tol.
let maxAngleStep (tol: float) (r: float) =
    if r <= 0.0 || tol >= r then Math.PI / 2.0
    else max 1e-3 (2.0 * acos (max -1.0 (1.0 - tol / r)))

/// SVG endpoint-parameterized elliptical arc -> sampled points (excluding p0).
let private flattenArc tol (p0: Pt) rx ry xRotDeg (largeArc: bool) (sweep: bool) (p1: Pt) (acc: ResizeArray<Pt>) =
    let rx = abs rx
    let ry = abs ry
    if p0.X = p1.X && p0.Y = p1.Y then ()
    elif rx < 1e-12 || ry < 1e-12 then acc.Add p1
    else
        let phi = xRotDeg * Math.PI / 180.0
        let cosPhi = cos phi
        let sinPhi = sin phi
        let dx2 = (p0.X - p1.X) / 2.0
        let dy2 = (p0.Y - p1.Y) / 2.0
        let x1p = cosPhi * dx2 + sinPhi * dy2
        let y1p = -sinPhi * dx2 + cosPhi * dy2
        // Scale radii up if the endpoints cannot be joined with the given radii.
        let lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
        let rx, ry = if lam > 1.0 then let s = sqrt lam in rx * s, ry * s else rx, ry
        let sign = if largeArc <> sweep then 1.0 else -1.0
        let num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
        let den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
        let co = if den = 0.0 then 0.0 else sign * sqrt (max 0.0 (num / den))
        let cxp = co * rx * y1p / ry
        let cyp = -co * ry * x1p / rx
        let cx = cosPhi * cxp - sinPhi * cyp + (p0.X + p1.X) / 2.0
        let cy = sinPhi * cxp + cosPhi * cyp + (p0.Y + p1.Y) / 2.0
        let angle ux uy vx vy =
            let d = sqrt ((ux * ux + uy * uy) * (vx * vx + vy * vy))
            if d = 0.0 then 0.0
            else
                let a = acos (max -1.0 (min 1.0 ((ux * vx + uy * vy) / d)))
                if ux * vy - uy * vx < 0.0 then -a else a
        let th1 = angle 1.0 0.0 ((x1p - cxp) / rx) ((y1p - cyp) / ry)
        let mutable dth = angle ((x1p - cxp) / rx) ((y1p - cyp) / ry) ((-x1p - cxp) / rx) ((-y1p - cyp) / ry)
        if not sweep && dth > 0.0 then dth <- dth - 2.0 * Math.PI
        elif sweep && dth < 0.0 then dth <- dth + 2.0 * Math.PI
        let n = max 2 (int (ceil (abs dth / maxAngleStep tol (max rx ry))))
        for k in 1 .. n do
            let th = th1 + dth * float k / float n
            let ex = rx * cos th
            let ey = ry * sin th
            acc.Add { X = cosPhi * ex - sinPhi * ey + cx; Y = sinPhi * ex + cosPhi * ey + cy }

/// Parse an SVG path `d` attribute and flatten every curve/arc into line
/// segments with chord error <= tol (in user units).
let parse (tol: float) (d: string) : Subpath list =
    let subpaths = ResizeArray<Subpath>()
    let mutable pts = ResizeArray<Pt>()
    let mutable cur = { X = 0.0; Y = 0.0 }
    let mutable start = { X = 0.0; Y = 0.0 }
    let mutable prevCubicCtrl: Pt option = None
    let mutable prevQuadCtrl: Pt option = None
    let mutable lastCmd = ' '
    let mutable i = 0
    let n = d.Length

    let isWs c = c = ' ' || c = '\t' || c = '\n' || c = '\r' || c = ','
    let skipWs () = while i < n && isWs d.[i] do i <- i + 1

    let readNumber () : float option =
        skipWs ()
        let s = i
        if i < n && (d.[i] = '+' || d.[i] = '-') then i <- i + 1
        let mutable digits = false
        while i < n && Char.IsDigit d.[i] do
            i <- i + 1
            digits <- true
        if i < n && d.[i] = '.' then
            i <- i + 1
            while i < n && Char.IsDigit d.[i] do
                i <- i + 1
                digits <- true
        if not digits then
            i <- s
            None
        else
            if i < n && (d.[i] = 'e' || d.[i] = 'E') then
                let e = i
                i <- i + 1
                if i < n && (d.[i] = '+' || d.[i] = '-') then i <- i + 1
                let mutable expDigits = false
                while i < n && Char.IsDigit d.[i] do
                    i <- i + 1
                    expDigits <- true
                if not expDigits then i <- e
            Some (float (d.Substring(s, i - s)))

    // Arc flags are single characters and may be run together ("11" = two flags).
    let readFlag () : bool option =
        skipWs ()
        if i < n && (d.[i] = '0' || d.[i] = '1') then
            let v = d.[i] = '1'
            i <- i + 1
            Some v
        else
            None

    let flush closed =
        if pts.Count > 1 then
            subpaths.Add { Points = pts.ToArray(); Closed = closed }
        pts <- ResizeArray<Pt>()

    // A draw command after Z starts a new subpath from the previous start point.
    let ensureStart () = if pts.Count = 0 then pts.Add cur

    let abort () = i <- n

    skipWs ()
    while i < n do
        skipWs ()
        if i < n then
            let c = d.[i]
            let isLetter = Char.IsLetter c
            if not isLetter && (lastCmd = ' ' || lastCmd = 'Z' || lastCmd = 'z') then
                abort ()
            else
                let cmd =
                    if isLetter then
                        i <- i + 1
                        c
                    else
                        match lastCmd with
                        | 'M' -> 'L'
                        | 'm' -> 'l'
                        | x -> x
                let rel = Char.IsLower cmd
                let toAbs x y = if rel then { X = cur.X + x; Y = cur.Y + y } else { X = x; Y = y }
                match Char.ToUpper cmd with
                | 'M' ->
                    match readNumber (), readNumber () with
                    | Some x, Some y ->
                        flush false
                        cur <- toAbs x y
                        start <- cur
                        pts.Add cur
                        prevCubicCtrl <- None
                        prevQuadCtrl <- None
                        lastCmd <- cmd
                    | _ -> abort ()
                | 'L' ->
                    match readNumber (), readNumber () with
                    | Some x, Some y ->
                        ensureStart ()
                        cur <- toAbs x y
                        pts.Add cur
                        prevCubicCtrl <- None
                        prevQuadCtrl <- None
                        lastCmd <- cmd
                    | _ -> abort ()
                | 'H' ->
                    match readNumber () with
                    | Some x ->
                        ensureStart ()
                        cur <- { X = (if rel then cur.X + x else x); Y = cur.Y }
                        pts.Add cur
                        prevCubicCtrl <- None
                        prevQuadCtrl <- None
                        lastCmd <- cmd
                    | _ -> abort ()
                | 'V' ->
                    match readNumber () with
                    | Some y ->
                        ensureStart ()
                        cur <- { X = cur.X; Y = (if rel then cur.Y + y else y) }
                        pts.Add cur
                        prevCubicCtrl <- None
                        prevQuadCtrl <- None
                        lastCmd <- cmd
                    | _ -> abort ()
                | 'C' ->
                    match readNumber (), readNumber (), readNumber (), readNumber (), readNumber (), readNumber () with
                    | Some x1, Some y1, Some x2, Some y2, Some x, Some y ->
                        ensureStart ()
                        let c1 = toAbs x1 y1
                        let c2 = toAbs x2 y2
                        let p = toAbs x y
                        flattenCubic tol 0 cur c1 c2 p pts
                        cur <- p
                        prevCubicCtrl <- Some c2
                        prevQuadCtrl <- None
                        lastCmd <- cmd
                    | _ -> abort ()
                | 'S' ->
                    match readNumber (), readNumber (), readNumber (), readNumber () with
                    | Some x2, Some y2, Some x, Some y ->
                        ensureStart ()
                        let c1 =
                            match prevCubicCtrl with
                            | Some pc -> { X = 2.0 * cur.X - pc.X; Y = 2.0 * cur.Y - pc.Y }
                            | None -> cur
                        let c2 = toAbs x2 y2
                        let p = toAbs x y
                        flattenCubic tol 0 cur c1 c2 p pts
                        cur <- p
                        prevCubicCtrl <- Some c2
                        prevQuadCtrl <- None
                        lastCmd <- cmd
                    | _ -> abort ()
                | 'Q' ->
                    match readNumber (), readNumber (), readNumber (), readNumber () with
                    | Some qx, Some qy, Some x, Some y ->
                        ensureStart ()
                        let q = toAbs qx qy
                        let p = toAbs x y
                        flattenQuad tol cur q p pts
                        cur <- p
                        prevQuadCtrl <- Some q
                        prevCubicCtrl <- None
                        lastCmd <- cmd
                    | _ -> abort ()
                | 'T' ->
                    match readNumber (), readNumber () with
                    | Some x, Some y ->
                        ensureStart ()
                        let q =
                            match prevQuadCtrl with
                            | Some pq -> { X = 2.0 * cur.X - pq.X; Y = 2.0 * cur.Y - pq.Y }
                            | None -> cur
                        let p = toAbs x y
                        flattenQuad tol cur q p pts
                        cur <- p
                        prevQuadCtrl <- Some q
                        prevCubicCtrl <- None
                        lastCmd <- cmd
                    | _ -> abort ()
                | 'A' ->
                    match readNumber (), readNumber (), readNumber (), readFlag (), readFlag (), readNumber (), readNumber () with
                    | Some rx, Some ry, Some rot, Some laf, Some sf, Some x, Some y ->
                        ensureStart ()
                        let p = toAbs x y
                        flattenArc tol cur rx ry rot laf sf p pts
                        cur <- p
                        prevCubicCtrl <- None
                        prevQuadCtrl <- None
                        lastCmd <- cmd
                    | _ -> abort ()
                | 'Z' ->
                    flush true
                    cur <- start
                    prevCubicCtrl <- None
                    prevQuadCtrl <- None
                    lastCmd <- cmd
                | _ -> abort ()
    flush false
    List.ofSeq subpaths
