module SvgExtrude.Types

type Pt = { X: float; Y: float }

/// A closed ring of 2D points (no duplicated closing point).
type Ring = Pt array

/// A fillable shape: one outer boundary plus zero or more holes.
type Shape = { Outer: Ring; Holes: Ring list }

/// One drawable SVG element, flattened into fillable shapes.
type ParsedElement =
    { Id: string
      Label: string
      Shapes: Shape list
      /// Count of unclosed subpaths that were auto-closed (surfaced as a warning).
      OpenSubpaths: int }

/// Per-path user settings. Color is preview-only (binary STL carries no color).
type Assignment = { Height: float; Color: string }
