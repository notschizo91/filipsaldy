module SvgExtrude.Main

open System
open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Browser.Dom
open Browser.Types
open SvgExtrude.Types
open SvgExtrude.SvgFlatten

[<Emit("parseFloat($0)")>]
let private parseFloatJs (s: string) : float = jsNative

[<Emit("$0.text().then($1)")>]
let private readFileText (file: obj) (cb: string -> unit) : unit = jsNative

let private byId (id: string) : HTMLElement = document.getElementById id
let private inputById (id: string) : HTMLInputElement = byId id :?> HTMLInputElement

let private svgNS = "http://www.w3.org/2000/svg"

// ---------------------------------------------------------------------------
// State
//
// The user builds *paths* (groups): each path has one height and one color,
// and holds any number of islands clicked in the SVG. One path is active;
// clicked shapes join it. "Start new path" begins a new group; clicking a
// path in the legend re-activates it so more shapes can be added later.
//
// Islands are keyed "<elementId>:<shapeIndex>" — one SVG element (e.g. a
// single vectorizer <path>) can contain many disconnected, individually
// selectable shapes.
// ---------------------------------------------------------------------------

let mutable private svgRoot: Element option = None
let mutable private fileName = "model"
let private parsed = Dictionary<string, ParsedElement>()
let private domEls = Dictionary<string, Element>()
let private labels = Dictionary<string, string>()

/// Group settings, keyed by group id.
let private groups = Dictionary<string, Assignment>()
let private groupNames = Dictionary<string, string>()
let private groupOrder = ResizeArray<string>()
let mutable private activeGroup: string option = None
let mutable private groupCounter = 0

/// Selected islands: island key -> owning group, plus geometry lookup and a
/// stable order for export.
let private membership = Dictionary<string, string>()
let private partsIndex = Dictionary<string, string * int>()
let private overlays = Dictionary<string, Element>()
let private order = ResizeArray<string>()

let mutable private tolerance = 0.5
let mutable private mmPerUnit = 1.0
/// Content bounds in SVG user units (minX, minY, maxX, maxY).
let mutable private contentBounds: (float * float * float * float) option = None
let mutable private viewer: obj = null
let mutable private colorCursor = 0

let private defaultColors =
    [| "#a855f7"; "#10b981"; "#f59e0b"; "#3b82f6"; "#ef4444"; "#ec4899"; "#22d3ee"; "#eab308" |]

let private defaultHeight = 2.0
let private minHeight = 0.2

let private partKey (elId: string) (idx: int) = elId + ":" + string idx

let private partShape (key: string) : Shape option =
    match partsIndex.TryGetValue key with
    | true, (elId, idx) when parsed.ContainsKey elId && idx < parsed.[elId].Shapes.Length ->
        Some parsed.[elId].Shapes.[idx]
    | _ -> None

let private groupOf (key: string) : string option =
    match membership.TryGetValue key with
    | true, g when groups.ContainsKey g -> Some g
    | _ -> None

let private membersOf (gid: string) : string array =
    order |> Seq.filter (fun k -> groupOf k = Some gid) |> Seq.toArray

// ---------------------------------------------------------------------------
// Geometry plumbing
// ---------------------------------------------------------------------------

/// SVG user units (y-down) -> centered millimetres (y-up). The same transform
/// feeds both the preview meshes and the STL export.
let private toMm (p: Pt) : Pt =
    match contentBounds with
    | Some (minX, minY, maxX, maxY) ->
        let cx = (minX + maxX) / 2.0
        let cy = (minY + maxY) / 2.0
        { X = (p.X - cx) * mmPerUnit; Y = (cy - p.Y) * mmPerUnit }
    | None -> { X = p.X * mmPerUnit; Y = -p.Y * mmPerUnit }

let private meshFor (key: string) : float array * int =
    match partShape key, groupOf key with
    | Some shape, Some gid -> Geometry.extrude (Geometry.mapShape toMm shape) groups.[gid].Height
    | _ -> [||], 0

let private recomputeBounds () =
    contentBounds <- Geometry.bounds (parsed.Values |> Seq.collect (fun pe -> pe.Shapes))

// ---------------------------------------------------------------------------
// Selection overlays (a highlight <path> per selected island, in group color)
// ---------------------------------------------------------------------------

let private shapeD (s: Shape) : string =
    let ringD (r: Ring) =
        "M " + (r |> Array.map (fun p -> sprintf "%g %g" p.X p.Y) |> String.concat " L ") + " Z"
    s.Outer :: s.Holes |> List.map ringD |> String.concat " "

let private overlayStyle (key: string) : string =
    let color =
        match groupOf key with
        | Some gid -> groups.[gid].Color
        | None -> "#a855f7"
    sprintf "fill:%s;fill-opacity:0.32;stroke:%s;filter:drop-shadow(0 0 4px %s)" color color color

let private removeOverlay (key: string) =
    match overlays.TryGetValue key with
    | true, node ->
        if not (isNull node.parentElement) then node.parentElement.removeChild node |> ignore
        overlays.Remove key |> ignore
    | _ -> ()

let private addOverlay (key: string) (shape: Shape) =
    match svgRoot with
    | Some root ->
        removeOverlay key
        let p = document.createElementNS (svgNS, "path")
        p.setAttribute ("d", shapeD shape)
        p.setAttribute ("class", "svgx-overlay")
        p.setAttribute ("fill-rule", "evenodd")
        p.setAttribute ("style", overlayStyle key)
        root.appendChild p |> ignore
        overlays.[key] <- p
    | None -> ()

let private restyleOverlay (key: string) =
    match overlays.TryGetValue key with
    | true, node -> node.setAttribute ("style", overlayStyle key)
    | _ -> ()

// ---------------------------------------------------------------------------
// UI updates
// ---------------------------------------------------------------------------

let private updateWarnings () =
    let box = byId "warnings"
    let msgs = ResizeArray<string>()
    let openTotal = parsed.Values |> Seq.sumBy (fun pe -> pe.OpenSubpaths)
    if openTotal > 0 then
        msgs.Add(
            sprintf "%d open subpath%s auto-closed — extrusions from these may not be watertight as drawn."
                openTotal (if openTotal = 1 then " was" else "s were"))
    let empty =
        domEls.Keys |> Seq.filter (fun id -> parsed.[id].Shapes.IsEmpty) |> Seq.length
    if empty > 0 then
        msgs.Add(sprintf "%d element%s no fillable area and can't be selected." empty (if empty = 1 then " has" else "s have"))
    if msgs.Count = 0 then
        box.setAttribute ("hidden", "")
    else
        box.removeAttribute "hidden"
        box.innerHTML <-
            msgs |> Seq.map (fun m -> "<div class=\"warning\">⚠ " + m + "</div>") |> String.concat ""

let private updateSizeReadout () =
    let el = byId "size-readout"
    let selectedShapes = order |> Seq.choose partShape
    match Geometry.bounds selectedShapes with
    | Some (minX, minY, maxX, maxY) when order.Count > 0 ->
        let w = (maxX - minX) * mmPerUnit
        let d = (maxY - minY) * mmPerUnit
        let h =
            order
            |> Seq.choose (fun k -> groupOf k |> Option.map (fun g -> groups.[g].Height))
            |> Seq.fold max 0.0
        el.textContent <- sprintf "%.1f × %.1f × %.1f mm" w d h
    | _ -> el.textContent <- "—"

let private updateExportState () =
    let btn = inputById "export-btn"
    btn.disabled <- order.Count = 0
    (byId "viewer-hint")?style?display <- if order.Count = 0 then "" else "none"

let private renderLegend () =
    let ul = byId "legend"
    (byId "legend-empty")?style?display <- if groupOrder.Count = 0 then "" else "none"
    let rows =
        groupOrder
        |> Seq.map (fun gid ->
            let a = groups.[gid]
            let count = (membersOf gid).Length
            sprintf
                """<li data-id="%s" class="%s"><span class="swatch" style="background:%s"></span><span class="l-name">%s</span><span class="l-count">%d part%s</span><span class="l-h">%.1f mm</span><button class="l-x" title="Delete this path">×</button></li>"""
                gid (if activeGroup = Some gid then "active" else "") a.Color groupNames.[gid]
                count (if count = 1 then "" else "s") a.Height)
        |> String.concat ""
    ul.innerHTML <- rows

let private renderEditor () =
    let panel = byId "editor"
    match activeGroup with
    | Some gid when groups.ContainsKey gid ->
        panel.removeAttribute "hidden"
        let a = groups.[gid]
        let count = (membersOf gid).Length
        (byId "editor-name").textContent <-
            sprintf "%s · %d part%s" groupNames.[gid] count (if count = 1 then "" else "s")
        (inputById "height-num").value <- string a.Height
        (inputById "height-range").value <- string a.Height
        (inputById "color-input").value <- a.Color
    | _ -> panel.setAttribute ("hidden", "")

let private refreshMesh (key: string) =
    match groupOf key with
    | Some gid when not (isNull viewer) ->
        let positions, _ = meshFor key
        Viewer.setMesh viewer key positions groups.[gid].Color
    | _ -> ()

let private refreshAll () =
    for key in order do
        refreshMesh key
    updateSizeReadout ()
    renderLegend ()

// ---------------------------------------------------------------------------
// Groups (user-facing "paths")
// ---------------------------------------------------------------------------

let private nextDefaultColor () = defaultColors.[colorCursor % defaultColors.Length]

let private newGroup (height: float) (color: string) : string =
    groupCounter <- groupCounter + 1
    let gid = sprintf "g%d" groupCounter
    groups.[gid] <- { Height = max minHeight height; Color = color }
    groupNames.[gid] <- sprintf "Path %d" groupCounter
    groupOrder.Add gid
    activeGroup <- Some gid
    // Suggest a fresh color for the next path.
    colorCursor <- colorCursor + 1
    (inputById "new-color").value <- nextDefaultColor ()
    renderEditor ()
    renderLegend ()
    gid

let private setActiveGroup (gid: string) =
    if groups.ContainsKey gid then
        activeGroup <- Some gid
        renderEditor ()
        renderLegend ()

let private removeIsland (key: string) =
    membership.Remove key |> ignore
    partsIndex.Remove key |> ignore
    order.Remove key |> ignore
    removeOverlay key
    if not (isNull viewer) then Viewer.removeMesh viewer key
    updateSizeReadout ()
    renderLegend ()
    renderEditor ()
    updateExportState ()

let private deleteGroup (gid: string) =
    for key in membersOf gid do
        removeIsland key
    groups.Remove gid |> ignore
    groupNames.Remove gid |> ignore
    groupOrder.Remove gid |> ignore
    if activeGroup = Some gid then
        activeGroup <- if groupOrder.Count > 0 then Some groupOrder.[groupOrder.Count - 1] else None
    renderLegend ()
    renderEditor ()
    updateSizeReadout ()
    updateExportState ()

/// A click on an island: add it to the active path, remove it if it is
/// already there, or move it over from another path.
let private clickIsland (elId: string) (idx: int) =
    let key = partKey elId idx
    let gid =
        match activeGroup with
        | Some g when groups.ContainsKey g -> g
        | _ -> newGroup defaultHeight (nextDefaultColor ())
    match membership.TryGetValue key with
    | true, current when current = gid -> removeIsland key
    | true, _ ->
        membership.[key] <- gid
        restyleOverlay key
        refreshMesh key
        updateSizeReadout ()
        renderLegend ()
        renderEditor ()
    | _ ->
        partsIndex.[key] <- (elId, idx)
        membership.[key] <- gid
        order.Add key
        match partShape key with
        | Some shape -> addOverlay key shape
        | None -> ()
        refreshMesh key
        if not (isNull viewer) then Viewer.fitView viewer
        updateSizeReadout ()
        renderLegend ()
        renderEditor ()
        updateExportState ()

// ---------------------------------------------------------------------------
// Click hit-testing (in root viewBox coordinates)
// ---------------------------------------------------------------------------

let private clientToSvg (svg: Element) (cx: float) (cy: float) : Pt option =
    let m: obj = svg?getScreenCTM ()
    if isNull m then None
    else
        let inv: obj = m?inverse ()
        let a: float = inv?a
        let b: float = inv?b
        let c: float = inv?c
        let d: float = inv?d
        let e: float = inv?e
        let f: float = inv?f
        Some { X = a * cx + c * cy + e; Y = b * cx + d * cy + f }

/// Find the topmost island under the point: elements in reverse document
/// order (later elements paint on top), point inside the outer ring and not
/// inside any hole.
let private hitTest (p: Pt) : (string * int) option =
    let ids = domEls.Keys |> Seq.toArray
    let mutable result = None
    for k in ids.Length - 1 .. -1 .. 0 do
        if result.IsNone then
            let elId = ids.[k]
            parsed.[elId].Shapes
            |> List.iteri (fun i s ->
                if result.IsNone
                   && Rings.contains s.Outer p
                   && not (s.Holes |> List.exists (fun h -> Rings.contains h p)) then
                    result <- Some (elId, i))
    result

// ---------------------------------------------------------------------------
// SVG loading
// ---------------------------------------------------------------------------

let private ensureViewBox (svg: Element) =
    if not (svg.hasAttribute "viewBox") then
        let w = parseFloatJs (svg.getAttribute "width")
        let h = parseFloatJs (svg.getAttribute "height")
        if not (Double.IsNaN w) && not (Double.IsNaN h) && w > 0.0 && h > 0.0 then
            svg.setAttribute ("viewBox", sprintf "0 0 %f %f" w h)
        else
            // Fall back to the rendered bounding box.
            let bb: obj = svg?getBBox ()
            svg.setAttribute ("viewBox", sprintf "%f %f %f %f" (bb?x: float) (bb?y: float) (bb?width: float) (bb?height: float))
    svg.removeAttribute "width"
    svg.removeAttribute "height"

let private sanitize (svg: Element) =
    let scripts = svg.querySelectorAll "script"
    for k in 0 .. scripts.length - 1 do
        let s = scripts.[k] :?> Element
        s.parentElement.removeChild s |> ignore
    let all = svg.querySelectorAll "*"
    for k in 0 .. all.length - 1 do
        let el = all.[k] :?> Element
        let attrs = el.attributes
        let doomed = ResizeArray<string>()
        for j in 0 .. attrs.length - 1 do
            let name = attrs.[j].name
            if name.StartsWith "on" then doomed.Add name
        for name in doomed do
            el.removeAttribute name

/// Re-flatten every element (e.g. after a tolerance change) and re-apply the
/// current selection to the new shapes.
let private reparseAll () =
    match svgRoot with
    | None -> ()
    | Some root ->
        for KeyValue (id, el) in domEls do
            parsed.[id] <- parseElement root tolerance id labels.[id] el
        recomputeBounds ()
        updateWarnings ()
        for key in order |> Seq.toArray do
            match partShape key with
            | Some shape -> addOverlay key shape
            | None -> removeIsland key // island disappeared at the new tolerance

let private loadSvg (name: string) (text: string) =
    fileName <- (let d = name.LastIndexOf '.' in if d > 0 then name.Substring(0, d) else name)
    // Reset state from any previous file.
    parsed.Clear ()
    domEls.Clear ()
    labels.Clear ()
    groups.Clear ()
    groupNames.Clear ()
    groupOrder.Clear ()
    membership.Clear ()
    partsIndex.Clear ()
    overlays.Clear ()
    order.Clear ()
    activeGroup <- None
    groupCounter <- 0
    colorCursor <- 0
    (inputById "new-color").value <- nextDefaultColor ()
    if not (isNull viewer) then Viewer.clearMeshes viewer

    let holder = byId "svg-holder"
    holder.innerHTML <- text
    let svg = holder.querySelector "svg"
    if isNull svg then
        window.alert "That file doesn't contain a valid <svg> element."
    else
        sanitize svg
        ensureViewBox svg
        svgRoot <- Some svg
        (byId "file-name").textContent <- name
        (byId "dropzone").setAttribute ("hidden", "")
        (byId "stage").removeAttribute "hidden"

        let drawables = svg.querySelectorAll "path, rect, circle, ellipse, polygon, polyline"
        let seen = HashSet<string>()
        for k in 0 .. drawables.length - 1 do
            let el = drawables.[k] :?> Element
            // Skip non-rendered definitions.
            if (el.closest "defs, clipPath, mask, symbol, pattern").IsNone then
                let baseId = if el.id <> "" then el.id else sprintf "svgx-%d" k
                let id =
                    if seen.Add baseId then baseId
                    else sprintf "%s-%d" baseId k
                seen.Add id |> ignore
                let label =
                    let tag = el.tagName.ToLower ()
                    if el.id <> "" then sprintf "%s (%s)" tag el.id else sprintf "%s #%d" tag (k + 1)
                el.setAttribute ("data-svgx-id", id)
                domEls.[id] <- el
                labels.[id] <- label
                parsed.[id] <- parseElement svg tolerance id label el
                if parsed.[id].Shapes.IsEmpty then el.classList.add "svgx-dead"

        // One listener on the root: hit-test the click point against every
        // island, topmost first. This keeps disconnected shapes inside a
        // single <path> individually selectable.
        svg.addEventListener (
            "click",
            fun ev ->
                let me = ev :?> MouseEvent
                match clientToSvg svg me.clientX me.clientY with
                | Some p ->
                    match hitTest p with
                    | Some (elId, idx) -> clickIsland elId idx
                    | None -> ()
                | None -> ()
        )

        recomputeBounds ()
        updateWarnings ()
        // The first path is ready to collect shapes right away; starting a
        // new one is never mandatory.
        newGroup defaultHeight (inputById "new-color").value |> ignore
        renderLegend ()
        renderEditor ()
        updateSizeReadout ()
        updateExportState ()

let private acceptFile (file: obj) =
    if not (isNull file) then
        let name: string = !!(file?name)
        if name.ToLower().EndsWith ".svg" then
            readFileText file (fun text -> loadSvg name text)
        else
            window.alert "Please choose an .svg file."

// ---------------------------------------------------------------------------
// Export
// ---------------------------------------------------------------------------

let private onExport () =
    if order.Count > 0 then
        let mutable degenerate = 0
        let parts =
            [ for key in order do
                let positions, degen = meshFor key
                degenerate <- degenerate + degen
                positions ]
        let buf = Stl.build parts
        Stl.download (fileName + ".stl") buf
        let openSel =
            order
            |> Seq.choose (fun key ->
                match partsIndex.TryGetValue key with
                | true, (elId, _) -> Some elId
                | _ -> None)
            |> Seq.distinct
            |> Seq.sumBy (fun elId -> parsed.[elId].OpenSubpaths)
        let note = byId "export-note"
        let tris = parts |> List.sumBy (fun p -> p.Length / 9)
        let extras =
            [ if openSel > 0 then sprintf "%d open subpath(s) auto-closed" openSel
              if degenerate > 0 then sprintf "%d degenerate triangle(s) dropped" degenerate ]
        note.textContent <-
            sprintf "Exported %d triangles%s" tris
                (if extras.IsEmpty then "" else " · ⚠ " + String.concat ", " extras)

// ---------------------------------------------------------------------------
// Wiring
// ---------------------------------------------------------------------------

let private init () =
    viewer <- Viewer.createViewer (byId "viewer-box")

    let fileInput = inputById "file-input"
    let dropzone = byId "dropzone"
    dropzone.addEventListener ("click", fun _ -> fileInput.click ())
    (byId "change-file").addEventListener ("click", fun _ -> fileInput.click ())
    fileInput.addEventListener (
        "change",
        fun _ ->
            let files: obj = fileInput?files
            acceptFile (files?item (0))
            fileInput.value <- ""
    )
    dropzone.addEventListener (
        "dragover",
        fun ev ->
            ev.preventDefault ()
            dropzone.classList.add "drag"
    )
    dropzone.addEventListener ("dragleave", fun _ -> dropzone.classList.remove "drag")
    dropzone.addEventListener (
        "drop",
        fun ev ->
            ev.preventDefault ()
            dropzone.classList.remove "drag"
            acceptFile (ev?dataTransfer?files?item (0))
    )

    // "Start new path": begins a fresh group with the chosen height + color.
    (byId "new-path-btn").addEventListener (
        "click",
        fun _ ->
            if svgRoot.IsSome then
                let h = parseFloatJs (inputById "new-height").value
                let h = if Double.IsNaN h then defaultHeight else h
                newGroup h (inputById "new-color").value |> ignore
    )

    // Height + color editor for the active path (applies to all its shapes).
    let heightNum = inputById "height-num"
    let heightRange = inputById "height-range"
    let colorInput = inputById "color-input"
    let applyHeight (v: float) =
        match activeGroup with
        | Some gid when groups.ContainsKey gid ->
            let h = if Double.IsNaN v then defaultHeight else max minHeight v
            groups.[gid] <- { groups.[gid] with Height = h }
            for key in membersOf gid do
                refreshMesh key
            updateSizeReadout ()
            renderLegend ()
        | _ -> ()
    heightNum.addEventListener (
        "input",
        fun _ ->
            heightRange.value <- heightNum.value
            applyHeight (parseFloatJs heightNum.value)
    )
    heightRange.addEventListener (
        "input",
        fun _ ->
            heightNum.value <- heightRange.value
            applyHeight (parseFloatJs heightRange.value)
    )
    colorInput.addEventListener (
        "input",
        fun _ ->
            match activeGroup with
            | Some gid when groups.ContainsKey gid ->
                groups.[gid] <- { groups.[gid] with Color = colorInput.value }
                for key in membersOf gid do
                    if not (isNull viewer) then Viewer.setColor viewer key colorInput.value
                    restyleOverlay key
                renderLegend ()
            | _ -> ()
    )

    // Legend: click a path to make it active again, × to delete it.
    (byId "legend").addEventListener (
        "click",
        fun ev ->
            let target = ev.target :?> Element
            match target.closest "[data-id]" with
            | Some row ->
                let gid = row.getAttribute "data-id"
                if target.classList.contains "l-x" then deleteGroup gid
                else setActiveGroup gid
            | None -> ()
    )

    // Advanced settings.
    let tolInput = inputById "tol-input"
    tolInput.addEventListener (
        "change",
        fun _ ->
            let v = parseFloatJs tolInput.value
            if not (Double.IsNaN v) && v > 0.0 then
                tolerance <- v
                reparseAll ()
                refreshAll ()
    )
    let mmInput = inputById "mm-input"
    mmInput.addEventListener (
        "change",
        fun _ ->
            let v = parseFloatJs mmInput.value
            if not (Double.IsNaN v) && v > 0.0 then
                mmPerUnit <- v
                refreshAll ()
                if order.Count > 0 && not (isNull viewer) then Viewer.fitView viewer
    )

    (byId "fit-btn").addEventListener ("click", fun _ -> if not (isNull viewer) then Viewer.fitView viewer)
    (byId "export-btn").addEventListener ("click", fun _ -> onExport ())

init ()
