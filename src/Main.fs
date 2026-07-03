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

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

let mutable private svgRoot: Element option = None
let mutable private fileName = "model"
let private parsed = Dictionary<string, ParsedElement>()
let private domEls = Dictionary<string, Element>()
let private labels = Dictionary<string, string>()
let private selection = Dictionary<string, Assignment>()
let private order = ResizeArray<string>()
let mutable private activeId: string option = None
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

let private meshFor (id: string) : float array * int =
    match parsed.TryGetValue id, selection.TryGetValue id with
    | (true, pe), (true, a) ->
        let shapes = pe.Shapes |> List.map (Geometry.mapShape toMm)
        Geometry.extrudeAll shapes a.Height
    | _ -> [||], 0

let private recomputeBounds () =
    contentBounds <- Geometry.bounds (parsed.Values |> Seq.collect (fun pe -> pe.Shapes))

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
    let selectedShapes =
        order |> Seq.collect (fun id -> parsed.[id].Shapes)
    match Geometry.bounds selectedShapes with
    | Some (minX, minY, maxX, maxY) when order.Count > 0 ->
        let w = (maxX - minX) * mmPerUnit
        let d = (maxY - minY) * mmPerUnit
        let h = order |> Seq.map (fun id -> selection.[id].Height) |> Seq.max
        el.textContent <- sprintf "%.1f × %.1f × %.1f mm" w d h
    | _ -> el.textContent <- "—"

let private updateExportState () =
    let btn = inputById "export-btn"
    btn.disabled <- order.Count = 0
    (byId "viewer-hint")?style?display <- if order.Count = 0 then "" else "none"

let private renderLegend () =
    let ul = byId "legend"
    (byId "legend-empty")?style?display <- if order.Count = 0 then "" else "none"
    let rows =
        order
        |> Seq.map (fun id ->
            let a = selection.[id]
            let active = (activeId = Some id)
            sprintf
                """<li data-id="%s" class="%s"><span class="swatch" style="background:%s"></span><span class="l-name">%s</span><span class="l-h">%.1f mm</span><button class="l-x" title="Remove from selection">×</button></li>"""
                id (if active then "active" else "") a.Color labels.[id] a.Height)
        |> String.concat ""
    ul.innerHTML <- rows

let private renderEditor () =
    let panel = byId "editor"
    match activeId with
    | Some id when selection.ContainsKey id ->
        panel.removeAttribute "hidden"
        let a = selection.[id]
        (byId "editor-name").textContent <- labels.[id]
        (inputById "height-num").value <- string a.Height
        (inputById "height-range").value <- string a.Height
        (inputById "color-input").value <- a.Color
    | _ -> panel.setAttribute ("hidden", "")

let private highlight (id: string) (on: bool) =
    match domEls.TryGetValue id with
    | true, el -> if on then el.classList.add "svgx-sel" else el.classList.remove "svgx-sel"
    | _ -> ()

let private refreshMesh (id: string) =
    if not (isNull viewer) then
        let positions, _ = meshFor id
        Viewer.setMesh viewer id positions selection.[id].Color

let private refreshAll () =
    for id in order do
        refreshMesh id
    updateSizeReadout ()
    renderLegend ()

// ---------------------------------------------------------------------------
// Selection
// ---------------------------------------------------------------------------

let private setActive (id: string option) =
    activeId <- id
    renderEditor ()
    renderLegend ()

let private select (id: string) =
    let color = defaultColors.[colorCursor % defaultColors.Length]
    colorCursor <- colorCursor + 1
    selection.[id] <- { Height = defaultHeight; Color = color }
    order.Add id
    highlight id true
    refreshMesh id
    if not (isNull viewer) then Viewer.fitView viewer
    setActive (Some id)
    updateSizeReadout ()
    updateExportState ()

let private deselect (id: string) =
    selection.Remove id |> ignore
    order.Remove id |> ignore
    highlight id false
    if not (isNull viewer) then Viewer.removeMesh viewer id
    if activeId = Some id then
        setActive (if order.Count > 0 then Some order.[order.Count - 1] else None)
    else
        renderLegend ()
    updateSizeReadout ()
    updateExportState ()

let private toggle (id: string) =
    if selection.ContainsKey id then
        if activeId = Some id then deselect id else setActive (Some id)
    else
        select id

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

let private reparseAll () =
    match svgRoot with
    | None -> ()
    | Some root ->
        for KeyValue (id, el) in domEls do
            parsed.[id] <- parseElement root tolerance id labels.[id] el
        recomputeBounds ()
        updateWarnings ()

let private loadSvg (name: string) (text: string) =
    fileName <- (let d = name.LastIndexOf '.' in if d > 0 then name.Substring(0, d) else name)
    // Reset state from any previous file.
    parsed.Clear ()
    domEls.Clear ()
    labels.Clear ()
    selection.Clear ()
    order.Clear ()
    activeId <- None
    colorCursor <- 0
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
                if parsed.[id].Shapes.IsEmpty then
                    el.classList.add "svgx-dead"
                else
                    el.addEventListener (
                        "click",
                        fun ev ->
                            ev.stopPropagation ()
                            toggle id
                    )
        recomputeBounds ()
        updateWarnings ()
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
            [ for id in order do
                let positions, degen = meshFor id
                degenerate <- degenerate + degen
                positions ]
        let buf = Stl.build parts
        Stl.download (fileName + ".stl") buf
        let openSel = order |> Seq.sumBy (fun id -> parsed.[id].OpenSubpaths)
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

    // Height + color editor for the active path.
    let heightNum = inputById "height-num"
    let heightRange = inputById "height-range"
    let colorInput = inputById "color-input"
    let applyHeight (v: float) =
        match activeId with
        | Some id when selection.ContainsKey id ->
            let h = if Double.IsNaN v then defaultHeight else max minHeight v
            selection.[id] <- { selection.[id] with Height = h }
            refreshMesh id
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
            match activeId with
            | Some id when selection.ContainsKey id ->
                selection.[id] <- { selection.[id] with Color = colorInput.value }
                if not (isNull viewer) then Viewer.setColor viewer id colorInput.value
                renderLegend ()
            | _ -> ()
    )

    // Legend: click a row to edit it, × to deselect.
    (byId "legend").addEventListener (
        "click",
        fun ev ->
            let target = ev.target :?> Element
            match target.closest "[data-id]" with
            | Some row ->
                let id = row.getAttribute "data-id"
                if target.classList.contains "l-x" then deselect id
                else setActive (Some id)
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
