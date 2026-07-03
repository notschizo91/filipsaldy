module SvgExtrude.Viewer

open Fable.Core
open Browser.Types

// Thin bindings over the three.js glue module. All scene management lives in
// viewer.js; F# only hands over triangle positions and colors.

[<Import("createViewer", "./viewer.js")>]
let createViewer (container: HTMLElement) : obj = jsNative

/// Create or replace the mesh for a path id (positions: 9 floats per triangle).
[<Import("setMesh", "./viewer.js")>]
let setMesh (viewer: obj) (id: string) (positions: float array) (color: string) : unit = jsNative

[<Import("setColor", "./viewer.js")>]
let setColor (viewer: obj) (id: string) (color: string) : unit = jsNative

[<Import("removeMesh", "./viewer.js")>]
let removeMesh (viewer: obj) (id: string) : unit = jsNative

[<Import("clearMeshes", "./viewer.js")>]
let clearMeshes (viewer: obj) : unit = jsNative

/// Frame the camera on the current meshes.
[<Import("fitView", "./viewer.js")>]
let fitView (viewer: obj) : unit = jsNative
