# SVG Extrude → STL

A web tool that turns an SVG into a 3D-printable model: upload an SVG, click the
paths you want, give each one a height (mm) and a preview color, inspect the
result in a live 3D view, and download a **binary STL**.

Companion tool to the "Image → SVG Vectorizer", using the same dark purple theme.

## Stack

- **F# → JavaScript via [Fable](https://fable.io)** for all application logic
- **three.js** for the 3D preview (orbit controls, per-mesh standard materials)
- **earcut** for 2D polygon triangulation (caps)
- **Vite** for dev server and bundling
- A hand-rolled SVG path parser/flattener in F# (`src/PathParser.fs`) — curves
  and arcs are adaptively sampled to straight segments at a configurable chord
  tolerance, which is what earcut needs

## Run it

Prerequisites: Node 18+, .NET SDK 8.

```bash
npm install
dotnet tool restore   # installs the Fable compiler (pinned in .config/dotnet-tools.json)
npm run dev           # Fable watch + Vite dev server on http://localhost:5173
```

Production build (outputs to `dist/`):

```bash
npm run build
```

End-to-end test (builds must exist; needs a Chromium binary, path via
`CHROMIUM_PATH`, defaults to `/opt/pw-browsers/chromium`):

```bash
npm run test:e2e
```

The test uploads `examples/sample.svg`, selects paths, assigns heights, exports
the STL, and validates the binary: byte layout, z-range, **watertightness**
(every edge shared by exactly two opposite-winding triangles), outward
orientation and total volume via signed-volume integration.

## How it works

1. **Upload** — drag & drop or browse (`.svg` only). The file is parsed with the
   browser's DOM parser and rendered inline so every element stays clickable
   (`<script>` tags and `on*` attributes are stripped first).
2. **Parse** — every `path`, `rect`, `circle`, `ellipse`, `polygon` and
   `polyline` is flattened into point rings at the curve tolerance (default
   0.5 SVG units, adjustable under *Advanced*). Element/group `transform`
   attributes are applied. Within one path, nested closed subpaths are
   classified by containment depth (even-odd): odd-depth rings become **holes**
   of their innermost containing ring. Open subpaths are auto-closed and a
   non-blocking warning is shown.
3. **Select** — click a path to select it (purple outline), click again while
   active to deselect. Each selection gets a height (default 2 mm, min 0.2 mm,
   slider + number input) and a color. The legend lists every selected path;
   click a row to edit it, `×` to remove it. Unselected paths are excluded from
   preview and export.
4. **Preview** — three.js scene, Z-up, orbit/pan/zoom, ambient + directional
   lights, per-path meshes rebuilt individually when their height changes
   (color changes only touch the material).
5. **Extrude** — `Geometry.extrude` is a pure function
   `(shape, height) → triangles`: earcut triangulates the top cap (holes
   respected), the bottom cap is the mirrored winding, and wall quads connect
   every boundary edge. Outer rings are normalized CCW and holes CW so all
   normals face outward. The exact same function feeds the preview and the
   export.
6. **Export** — one combined binary STL (80-byte header, uint32 triangle count,
   50 bytes per triangle, little-endian), downloaded via a Blob. The scale
   (mm per SVG unit, default 1) applies identically to X/Y and the heights.

## Decisions on the spec's open questions

1. **Overlapping shapes** intersect as-is; boolean carve-outs (taller cuts into
   shorter) would need CSG and are flagged as future work.
2. **Single combined STL** for v1. Color is preview-only — binary STL has no
   standard per-face color; for multi-color prints, exporting one STL per color
   group is the reliable path (stretch feature).
3. **No Web Worker** for v1; parsing + triangulation are fast at typical sizes.
   Very complex SVGs (hundreds of paths) may warrant moving triangulation off
   the main thread later.

## Project layout

```
index.html            static shell (panels, inputs)
styles.css            theme (palette tokens from the vectorizer tool)
src/App.fsproj        F# project (compile order matters)
src/Types.fs          domain types
src/PathParser.fs     SVG path `d` parser + adaptive curve/arc flattening
src/Rings.fs          ring cleanup, area/containment, hole classification
src/SvgFlatten.fs     DOM-side flattening of shape elements + transforms
src/Geometry.fs       earcut interop, pure extrusion (caps + walls)
src/Stl.fs            binary STL writer + download
src/Viewer.fs         F# bindings for the viewer
src/viewer.js         three.js glue (scene, controls, lights, mesh registry)
src/Main.fs           state, selection, UI wiring
examples/sample.svg   demo file (donut with hole, transforms, open path)
tests/e2e.mjs         headless end-to-end test with STL validation
```
