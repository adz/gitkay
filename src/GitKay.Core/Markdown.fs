namespace GitKay.Core

open System
open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines
open Markdig.Extensions.Tables
open Markdig.Extensions.Footnotes
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text

type MarkdownListMarker =
    | Bullet of char
    | Ordered of int

type MarkdownAlignment =
    | Default
    | Left
    | Center
    | Right

type MarkdownInline =
    | Text of string
    | Emphasis of MarkdownInline list
    | Strong of MarkdownInline list
    | Strikethrough of MarkdownInline list
    | Code of string
    | Link of target: string * MarkdownInline list
    | Image of source: string * alt: string * title: string
    | LineBreak
    | Html of string

type MarkdownBlock =
    | Heading of level: int * MarkdownInline list
    | Paragraph of MarkdownInline list
    | ListItem of depth: int * marker: MarkdownListMarker * ``checked``: bool option * MarkdownBlock list
    | Quote of MarkdownBlock list
    | CodeBlock of language: string * text: string
    | Table of header: MarkdownInline list list * alignments: MarkdownAlignment list * rows: MarkdownInline list list list
    | Rule
    | HtmlBlock of string
    | Footnote of label: string * MarkdownBlock list

type LocatedMarkdownBlock =
    { Block: MarkdownBlock
      FirstLine: int
      LastLine: int }

type MarkdownWordSpanKind = Kept | Inserted | Deleted

type MarkdownSpanStyle = Plain = 0 | Emphasis = 1 | Strong = 2 | Strikethrough = 3 | Code = 4 | Link = 5 | Html = 6

type MarkdownWordSpan =
    { Kind: MarkdownWordSpanKind
      Text: string
      Style: MarkdownSpanStyle
      Target: string option }

type MarkdownLeafChange =
    | Unchanged of LocatedMarkdownBlock
    | Added of LocatedMarkdownBlock
    | Removed of LocatedMarkdownBlock
    | Modified of old: LocatedMarkdownBlock * current: LocatedMarkdownBlock * words: MarkdownWordSpan list
    | MovedFrom of previous: LocatedMarkdownBlock * destination: LocatedMarkdownBlock
    | MovedTo of source: LocatedMarkdownBlock * current: LocatedMarkdownBlock

type MarkdownChangeKind = Unchanged = 0 | Added = 1 | Removed = 2 | Modified = 3 | Moved = 4

type RenderedMarkdownSpan =
    { Text: string
      Style: MarkdownSpanStyle
      Target: string option }

type MarkdownCodeLine =
    { Previous: string option
      Current: string option
      Change: MarkdownChangeKind }

type RenderedMarkdownRow =
    { Change: MarkdownChangeKind
      Current: LocatedMarkdownBlock option
      Previous: LocatedMarkdownBlock option
      CurrentText: string
      PreviousText: string
      Source: string
      PreviousSpans: RenderedMarkdownSpan list
      CurrentSpans: RenderedMarkdownSpan list
      Words: MarkdownWordSpan list
      CodeLines: MarkdownCodeLine list
      MoveCounterpartLine: int option }

type RenderedMarkdownDisplayRow =
    | RenderedBlock of RenderedMarkdownRow
    | UnchangedSections of int

type MarkdownTarget =
    | RepositoryPath of string
    | HeadingTarget of string
    | RemoteUrl of string
    | InvalidTarget of string

type MarkdownImageSide = Old | New

type MarkdownImageRequest =
    { Side: MarkdownImageSide
      Source: string
      Target: MarkdownTarget }

type MarkdownImageData =
    { Side: MarkdownImageSide
      Source: string
      Bytes: byte array option
      Error: string option }

type RenderedMarkdownContent =
    { File: Models.FileDiff
      DiffRows: RenderedMarkdownRow list
      OldRows: RenderedMarkdownRow list
      NewRows: RenderedMarkdownRow list
      Images: MarkdownImageData list }

/// <summary>A source format GitKay can reformat for reading. Closed, because every case needs a formatter.</summary>
type PreviewFormat =
    | JsonFormat
    | XmlFormat

type PreviewKind =
    | MarkdownPreview
    | ImagePreview of format: string
    /// <summary>Source that is easier to read reformatted, such as JSON written on one line.</summary>
    | FormattedPreview of format: PreviewFormat
    | SourceOnly

[<RequireQualifiedAccess>]
module Markdown =
    let private pipeline =
        MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseYamlFrontMatter()
            .Build()

    let rec private plainInlines (container: ContainerInline) =
        [ let mutable current = container.FirstChild
          while not (isNull current) do
              match current with
              | :? LiteralInline as literal -> yield Text(literal.Content.ToString())
              | :? CodeInline as code -> yield Code code.Content
              | :? LineBreakInline -> yield LineBreak
              | :? HtmlInline as html -> yield Html html.Tag
              | :? FootnoteLink as footnoteLink ->
                  let label = footnoteLink.Footnote.Label.TrimStart '^'
                  if footnoteLink.IsBackLink then yield Link($"#fnref-{label}", [ Text "↩" ])
                  else yield Link($"#fn-{label}", [ Text $"[{label}]" ])
              // The owning list item carries task state so the renderer can draw one marker without
              // contaminating its semantic text or diff fingerprint.
              | :? Markdig.Extensions.TaskLists.TaskList -> ()
              | :? LinkInline as link when link.IsImage ->
                  let alt =
                      plainInlines link
                      |> List.choose (function Text value -> Some value | _ -> None)
                      |> String.concat ""
                  yield Image((if isNull link.Url then "" else link.Url), alt, (if isNull link.Title then "" else link.Title))
              | :? LinkInline as link ->
                  yield Link((if isNull link.Url then "" else link.Url), plainInlines link)
              | :? EmphasisInline as emphasis ->
                  let children = plainInlines emphasis
                  if emphasis.DelimiterChar = '~' then yield Strikethrough children
                  elif emphasis.DelimiterCount >= 2 then yield Strong children
                  else yield Emphasis children
              | :? ContainerInline as nested -> yield! plainInlines nested
              | _ -> ()
              current <- current.NextSibling ]

    let private inlines (leaf: LeafBlock) =
        if isNull leaf.Inline then [] else plainInlines leaf.Inline

    let private leafText (leaf: LeafBlock) =
        if isNull leaf.Lines.Lines then "" else leaf.Lines.ToString()

    let rec private blocks depth (container: ContainerBlock) : MarkdownBlock list =
        [ for child in container do
              match child with
              | :? Markdig.Extensions.Yaml.YamlFrontMatterBlock -> ()
              | :? HeadingBlock as heading -> yield Heading(heading.Level, inlines heading)
              | :? ParagraphBlock as paragraph -> yield Paragraph(inlines paragraph)
              | :? Markdig.Syntax.FencedCodeBlock as code -> yield CodeBlock((if isNull code.Info then "" else code.Info), leafText code)
              | :? Markdig.Syntax.CodeBlock as code -> yield CodeBlock("", leafText code)
              | :? QuoteBlock as quote -> yield Quote(blocks (depth + 1) quote)
              | :? Footnote as footnote -> yield Footnote(footnote.Label.TrimStart '^', blocks depth footnote)
              | :? FootnoteGroup as group ->
                  for footnoteObject in group do
                      match footnoteObject with
                      | :? Footnote as footnote -> yield Footnote(footnote.Label.TrimStart '^', blocks depth footnote)
                      | _ -> ()
              | :? ListBlock as list ->
                  let mutable number = match Int32.TryParse list.OrderedStart with true, value -> value | _ -> 1
                  for itemObject in list do
                      match itemObject with
                      | :? ListItemBlock as item ->
                          let marker = if list.IsOrdered then let n = number in number <- number + 1; Ordered n else Bullet list.BulletType
                          let checkedState =
                              item
                              |> Seq.tryPick (function
                                  | :? ParagraphBlock as paragraph when not (isNull paragraph.Inline) ->
                                      let mutable currentInline = paragraph.Inline.FirstChild
                                      let mutable found = None
                                      while found.IsNone && not (isNull currentInline) do
                                          match currentInline with
                                          | :? Markdig.Extensions.TaskLists.TaskList as task -> found <- Some task.Checked
                                          | _ -> currentInline <- currentInline.NextSibling
                                      found
                                  | _ -> None)
                          let itemBlocks = blocks (depth + 1) item
                          let itemBlocks =
                              match checkedState, itemBlocks with
                              | Some _, Paragraph(Text text :: rest) :: tail -> Paragraph(Text(text.TrimStart()) :: rest) :: tail
                              | _ -> itemBlocks
                          yield ListItem(depth, marker, checkedState, itemBlocks)
                      | _ -> ()
              | :? ThematicBreakBlock -> yield Rule
              | :? HtmlBlock as html -> yield HtmlBlock(leafText html)
              | :? Markdig.Extensions.Tables.Table as table ->
                  let rows =
                      [ for rowObject in table do
                            match rowObject with
                            | :? TableRow as row ->
                                yield
                                    [ for cellObject in row do
                                          match cellObject with
                                          | :? TableCell as cell ->
                                              let cellBlocks = blocks depth cell
                                              yield
                                                  cellBlocks
                                                  |> List.collect (function Paragraph xs | Heading(_, xs) -> xs | _ -> [])
                                          | _ -> () ]
                            | _ -> () ]
                  let header, body = if rows.IsEmpty then [], [] else rows.Head, rows.Tail
                  let alignments =
                      table.ColumnDefinitions
                      |> Seq.map (fun column ->
                          if not column.Alignment.HasValue then Default
                          else
                              match column.Alignment.Value with
                              | TableColumnAlign.Left -> Left
                              | TableColumnAlign.Center -> Center
                              | TableColumnAlign.Right -> Right
                              | _ -> Default)
                      |> Seq.toList
                  yield Table(header, alignments, body)
              | :? ContainerBlock as nested -> yield! blocks depth nested
              | :? LeafBlock as leaf when not (isNull leaf.Inline) -> yield Paragraph(inlines leaf)
              | _ -> () ]

    let private located (source: string) (document: MarkdownDocument) =
        let model = blocks 0 document
        let syntaxBlocks =
            [ for syntax in document do
                  match syntax with
                  | :? Markdig.Extensions.Yaml.YamlFrontMatterBlock -> ()
                  | :? ListBlock as list ->
                      for item in list do
                          match item with :? ListItemBlock as listItem -> yield listItem :> Block | _ -> ()
                  | _ -> yield syntax ]
            |> List.toArray
        model
        |> List.mapi (fun index block ->
            if syntaxBlocks.Length = 0 then { Block = block; FirstLine = 1; LastLine = 1 }
            else
                let syntax = syntaxBlocks[min index (syntaxBlocks.Length - 1)]
                let first = max 0 syntax.Line
                let spanText =
                    if syntax.Span.Start < 0 || syntax.Span.End < syntax.Span.Start || syntax.Span.Start >= source.Length then ""
                    else source.Substring(syntax.Span.Start, min (source.Length - syntax.Span.Start) (syntax.Span.Length))
                { Block = block; FirstLine = first + 1; LastLine = first + 1 + (spanText |> Seq.filter ((=) '\n') |> Seq.length) })

    let rec inlineText = function
        | Text value | Code value | Html value -> value
        | Emphasis children | Strong children | Strikethrough children -> children |> List.map inlineText |> String.concat ""
        | Link(_, children) -> children |> List.map inlineText |> String.concat ""
        | Image(_, alt, _) -> alt
        | LineBreak -> "\n"

    let rec blockText = function
        | Heading(_, children) | Paragraph children -> children |> List.map inlineText |> String.concat ""
        | ListItem(_, _, _, children) | Quote children | Footnote(_, children) -> children |> List.map blockText |> String.concat "\n"
        | CodeBlock(_, text) | HtmlBlock text -> text
        | Table(header, _, rows) ->
            (header :: rows) |> List.collect id |> List.collect id |> List.map inlineText |> String.concat " "
        | Rule -> "---"

    let private kind = function
        | Heading _ -> "heading" | Paragraph _ -> "paragraph" | ListItem _ -> "list" | Quote _ -> "quote"
        | CodeBlock _ -> "code" | Table _ -> "table" | Rule -> "rule" | HtmlBlock _ -> "html" | Footnote _ -> "footnote"

    let private fingerprint located =
        let collapsed = Text.RegularExpressions.Regex.Replace(blockText located.Block, @"\s+", " ").Trim()
        let shape =
            match located.Block with
            | ListItem(depth, marker, _, _) ->
                let markerShape = match marker with Bullet value -> string value | Ordered _ -> "ordered"
                $"list:{depth}:{markerShape}"
            | Quote _ -> "quote"
            | Footnote(label, _) -> "footnote:" + label
            | other -> kind other
        shape + "|" + collapsed

    let private tokens text =
        Text.RegularExpressions.Regex.Matches(text, @"\w+|[^\w\s]") |> Seq.map _.Value |> Seq.toArray

    let private styledTokens block =
        let rec inlineTokens style target = function
            | Text value -> tokens value |> Array.map (fun text -> text, style, target) |> Array.toList
            | Code value -> tokens value |> Array.map (fun text -> text, MarkdownSpanStyle.Code, None) |> Array.toList
            | Html value -> tokens value |> Array.map (fun text -> text, MarkdownSpanStyle.Html, None) |> Array.toList
            | LineBreak -> []
            | Image(_, alt, _) -> tokens alt |> Array.map (fun text -> text, MarkdownSpanStyle.Emphasis, None) |> Array.toList
            | Emphasis children -> children |> List.collect (inlineTokens MarkdownSpanStyle.Emphasis target)
            | Strong children -> children |> List.collect (inlineTokens MarkdownSpanStyle.Strong target)
            | Strikethrough children -> children |> List.collect (inlineTokens MarkdownSpanStyle.Strikethrough target)
            | Link(linkTarget, children) -> children |> List.collect (inlineTokens MarkdownSpanStyle.Link (Some linkTarget))
        let rec fromBlock = function
            | Heading(_, children) | Paragraph children -> children |> List.collect (inlineTokens MarkdownSpanStyle.Plain None)
            | ListItem(_, _, _, children) | Quote children | Footnote(_, children) -> children |> List.collect fromBlock
            | other -> tokens (blockText other) |> Array.map (fun text -> text, MarkdownSpanStyle.Plain, None) |> Array.toList
        fromBlock block |> List.toArray

    let private wordChanges oldBlock newBlock =
        GitKay.Kit.Myers.diffBy (fun (oldText, _, _) (newText, _, _) -> oldText = newText) (styledTokens oldBlock) (styledTokens newBlock)
        |> List.map (function
            | GitKay.Kit.Equal(_, (text, style, target)) -> { Kind = Kept; Text = text; Style = style; Target = target }
            | GitKay.Kit.Delete(text, style, target) -> { Kind = Deleted; Text = text; Style = style; Target = target }
            | GitKay.Kit.Insert(text, style, target) -> { Kind = Inserted; Text = text; Style = style; Target = target })

    let private similar oldLeaf newLeaf =
        if kind oldLeaf.Block <> kind newLeaf.Block then false
        else
            let oldWords, newWords = tokens (blockText oldLeaf.Block), tokens (blockText newLeaf.Block)
            let shared =
                GitKay.Kit.Myers.diff oldWords newWords
                |> List.sumBy (function GitKay.Kit.Equal _ -> 1 | _ -> 0)
            shared * 2 >= max oldWords.Length newWords.Length

    let private changedRun (removed: LocatedMarkdownBlock list) (added: LocatedMarkdownBlock list) =
        let remaining = ResizeArray<LocatedMarkdownBlock>(added)
        [ for oldLeaf in removed do
              let index = remaining |> Seq.tryFindIndex (similar oldLeaf)
              match index with
              | Some index ->
                  let current = remaining[index]
                  remaining.RemoveAt index
                  yield Modified(oldLeaf, current, wordChanges oldLeaf.Block current.Block)
              | None -> yield Removed oldLeaf
          for current in remaining do yield Added current ]

    /// Myers alignment over block fingerprints, followed by same-kind similarity pairing within changed runs.
    let align (oldBlocks: LocatedMarkdownBlock list) (newBlocks: LocatedMarkdownBlock list) =
        let edits = GitKay.Kit.Myers.diffBy (fun oldLeaf newLeaf -> fingerprint oldLeaf = fingerprint newLeaf) (List.toArray oldBlocks) (List.toArray newBlocks)
        let output = ResizeArray<MarkdownLeafChange>()
        let removed, added = ResizeArray<LocatedMarkdownBlock>(), ResizeArray<LocatedMarkdownBlock>()
        let flush () =
            if removed.Count > 0 || added.Count > 0 then
                output.AddRange(changedRun (List.ofSeq removed) (List.ofSeq added))
                removed.Clear(); added.Clear()
        for edit in edits do
            match edit with
            | GitKay.Kit.Equal(_, current) -> flush (); output.Add(Unchanged current)
            | GitKay.Kit.Delete oldLeaf -> removed.Add oldLeaf
            | GitKay.Kit.Insert current -> added.Add current
        flush ()
        let aligned = output.ToArray()
        let availableAdds =
            aligned
            |> Array.mapi (fun index change -> index, change)
            |> Array.choose (function index, Added current -> Some(index, current) | _ -> None)
            |> ResizeArray
        for removedIndex in 0 .. aligned.Length - 1 do
            match aligned[removedIndex] with
            | Removed previous ->
                match availableAdds |> Seq.tryFindIndex (fun (_, current) -> fingerprint previous = fingerprint current) with
                | Some availableIndex ->
                    let addedIndex, current = availableAdds[availableIndex]
                    availableAdds.RemoveAt availableIndex
                    aligned[removedIndex] <- MovedFrom(previous, current)
                    aligned[addedIndex] <- MovedTo(previous, current)
                | None -> ()
            | _ -> ()
        List.ofArray aligned

    let rec private renderedSpans style target = function
        | Text value -> [ { Text = value; Style = style; Target = target } ]
        | Code value -> [ { Text = value; Style = MarkdownSpanStyle.Code; Target = None } ]
        | Html value -> [ { Text = value; Style = MarkdownSpanStyle.Html; Target = None } ]
        | LineBreak -> [ { Text = "\n"; Style = style; Target = target } ]
        | Image(_, alt, _) -> [ { Text = alt; Style = MarkdownSpanStyle.Emphasis; Target = None } ]
        | Emphasis children -> children |> List.collect (renderedSpans MarkdownSpanStyle.Emphasis target)
        | Strong children -> children |> List.collect (renderedSpans MarkdownSpanStyle.Strong target)
        | Strikethrough children -> children |> List.collect (renderedSpans MarkdownSpanStyle.Strikethrough target)
        | Link(linkTarget, children) -> children |> List.collect (renderedSpans MarkdownSpanStyle.Link (Some linkTarget))

    let rec private blockSpans block =
        let plain text = { Text = text; Style = MarkdownSpanStyle.Plain; Target = None }
        match block with
        | Heading(_, children) | Paragraph children -> children |> List.collect (renderedSpans MarkdownSpanStyle.Plain None)
        | ListItem(depth, marker, isChecked, children) ->
            let markerText =
                match isChecked, marker with
                | Some value, _ -> if value then "☑ " else "☐ "
                | None, Ordered number -> $"{number}. "
                | None, Bullet _ -> "• "
            plain (String.replicate depth "  " + markerText) :: (children |> List.collect blockSpans)
        | Quote children -> plain "│ " :: (children |> List.collect blockSpans)
        | Footnote(label, children) -> plain $"[{label}] " :: (children |> List.collect blockSpans)
        | _ -> [ plain (blockText block) ]

    let private sourceSlice (source: string) located =
        let lines = source.Replace("\r\n", "\n").Split '\n'
        let first = Math.Clamp(located.FirstLine - 1, 0, lines.Length)
        let count = Math.Clamp(located.LastLine - located.FirstLine + 1, 0, lines.Length - first)
        lines |> Array.skip first |> Array.take count |> String.concat "\n"

    let private codeLines (previous: string) (current: string) =
        let lines (text: string) = text.Replace("\r\n", "\n").Split '\n'
        let edits = GitKay.Kit.Myers.diff (lines previous) (lines current)
        let result = ResizeArray<MarkdownCodeLine>()
        let removed, added = ResizeArray<string>(), ResizeArray<string>()
        let flush () =
            let paired = min removed.Count added.Count
            for index in 0 .. paired - 1 do
                result.Add { Previous = Some removed[index]; Current = Some added[index]; Change = MarkdownChangeKind.Modified }
            for index in paired .. removed.Count - 1 do
                result.Add { Previous = Some removed[index]; Current = None; Change = MarkdownChangeKind.Removed }
            for index in paired .. added.Count - 1 do
                result.Add { Previous = None; Current = Some added[index]; Change = MarkdownChangeKind.Added }
            removed.Clear(); added.Clear()
        for edit in edits do
            match edit with
            | GitKay.Kit.Equal(oldLine, currentLine) -> flush (); result.Add { Previous = Some oldLine; Current = Some currentLine; Change = MarkdownChangeKind.Unchanged }
            | GitKay.Kit.Delete line -> removed.Add line
            | GitKay.Kit.Insert line -> added.Add line
        flush ()
        List.ofSeq result

    let projectRows oldSource newSource changes =
        changes
        |> List.map (function
            | Unchanged current ->
                { Change = MarkdownChangeKind.Unchanged; Current = Some current; Previous = Some current
                  CurrentText = blockText current.Block; PreviousText = blockText current.Block
                  Source = sourceSlice newSource current; PreviousSpans = blockSpans current.Block; CurrentSpans = blockSpans current.Block; Words = []; CodeLines = []; MoveCounterpartLine = None }
            | Added current ->
                { Change = MarkdownChangeKind.Added; Current = Some current; Previous = None
                  CurrentText = blockText current.Block; PreviousText = ""
                  Source = sourceSlice newSource current; PreviousSpans = []; CurrentSpans = blockSpans current.Block; Words = []; CodeLines = []; MoveCounterpartLine = None }
            | Removed previous ->
                { Change = MarkdownChangeKind.Removed; Current = None; Previous = Some previous
                  CurrentText = ""; PreviousText = blockText previous.Block
                  Source = sourceSlice oldSource previous; PreviousSpans = blockSpans previous.Block; CurrentSpans = []; Words = []; CodeLines = []; MoveCounterpartLine = None }
            | Modified(previous, current, words) ->
                { Change = MarkdownChangeKind.Modified; Current = Some current; Previous = Some previous
                  CurrentText = blockText current.Block; PreviousText = blockText previous.Block
                  Source = sourceSlice newSource current; PreviousSpans = blockSpans previous.Block; CurrentSpans = blockSpans current.Block; Words = words
                  CodeLines =
                    match previous.Block, current.Block with
                    | CodeBlock(_, oldText), CodeBlock(_, newText) -> codeLines oldText newText
                    | _ -> []
                  MoveCounterpartLine = None }
            | MovedFrom(previous, destination) ->
                { Change = MarkdownChangeKind.Moved; Current = None; Previous = Some previous
                  CurrentText = ""; PreviousText = blockText previous.Block; Source = sourceSlice oldSource previous
                  PreviousSpans = blockSpans previous.Block; CurrentSpans = []; Words = []; CodeLines = []
                  MoveCounterpartLine = Some destination.FirstLine }
            | MovedTo(source, current) ->
                { Change = MarkdownChangeKind.Moved; Current = Some current; Previous = None
                  CurrentText = blockText current.Block; PreviousText = ""; Source = sourceSlice newSource current
                  PreviousSpans = []; CurrentSpans = blockSpans current.Block; Words = []; CodeLines = []
                  MoveCounterpartLine = Some source.FirstLine })

    /// Collapse only long unchanged runs, retaining context on both sides of changes.
    let changesOnly context (rows: RenderedMarkdownRow list) =
        let context = max 0 context
        let output = ResizeArray<RenderedMarkdownDisplayRow>()
        let unchanged = ResizeArray<RenderedMarkdownRow>()
        let flush () =
            if unchanged.Count <= context * 2 + 1 then
                unchanged |> Seq.iter (RenderedBlock >> output.Add)
            else
                unchanged |> Seq.take context |> Seq.iter (RenderedBlock >> output.Add)
                output.Add(UnchangedSections(unchanged.Count - context * 2))
                unchanged |> Seq.skip (unchanged.Count - context) |> Seq.iter (RenderedBlock >> output.Add)
            unchanged.Clear()
        for row in rows do
            if row.Change = MarkdownChangeKind.Unchanged then unchanged.Add row
            else flush (); output.Add(RenderedBlock row)
        flush ()
        List.ofSeq output

    let previewKind (path: string) =
        match IO.Path.GetExtension(path).ToLowerInvariant() with
        | ".md" | ".markdown" | ".mdown" | ".mkd" -> MarkdownPreview
        | ".png" | ".jpg" | ".jpeg" | ".gif" | ".bmp" | ".webp" as format -> ImagePreview(format.TrimStart '.')
        | ".json" -> FormattedPreview JsonFormat
        | ".xml" | ".xsd" | ".xsl" | ".xslt" | ".svg" | ".axaml" | ".xaml"
        | ".csproj" | ".fsproj" | ".props" | ".targets" | ".slnx" | ".nuspec" -> FormattedPreview XmlFormat
        | _ -> SourceOnly

    /// <summary>
    /// Reformats a document for reading. Minified JSON is one enormous line; indenting it is the whole point of
    /// previewing it. Anything that will not parse is left exactly as it was, so a preview never hides the file.
    /// </summary>
    let formatForPreview (format: PreviewFormat) (text: string) =
        if String.IsNullOrWhiteSpace text then Error "There is nothing to reformat"
        else

        match format with
        | XmlFormat ->
            try
                // Insignificant whitespace is dropped on the way in and written back to one fixed shape, so the
                // same document always formats identically no matter how it was indented when it was committed.
                let settings = Xml.XmlWriterSettings(Indent = true, IndentChars = "  ", NewLineChars = "\n", OmitXmlDeclaration = true)
                let document = Xml.Linq.XDocument.Parse(text, Xml.Linq.LoadOptions.None)
                use writer = new IO.StringWriter()
                use xml = Xml.XmlWriter.Create(writer, settings)
                document.Save xml
                xml.Flush()
                let body = writer.ToString()
                // The declaration is part of the file; keep the one it had rather than inventing or dropping one.
                match document.Declaration with
                | null -> Ok body
                | declaration -> Ok(string declaration + "\n" + body)
            with _ -> Error "This file is not well-formed XML"
        | JsonFormat ->
            try
                use document = Text.Json.JsonDocument.Parse(text, Text.Json.JsonDocumentOptions(AllowTrailingCommas = true, CommentHandling = Text.Json.JsonCommentHandling.Skip))
                use stream = new IO.MemoryStream()
                use writer = new Text.Json.Utf8JsonWriter(stream, Text.Json.JsonWriterOptions(Indented = true))
                document.WriteTo writer
                writer.Flush()
                Ok(Text.Encoding.UTF8.GetString(stream.ToArray()))
            with _ -> Error "This file is not valid JSON"

    let resolveTarget (documentPath: string) (target: string) =
        if String.IsNullOrWhiteSpace target then InvalidTarget target
        elif target.StartsWith "#" then HeadingTarget(target.Substring 1)
        else
            match Uri.TryCreate(target, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> RemoteUrl target
            | _ ->
                let basePath = if target.StartsWith "/" then "" else defaultArg (Option.ofObj (IO.Path.GetDirectoryName documentPath)) ""
                let parts = (basePath.Replace('\\', '/') + "/" + target.TrimStart '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                let normalized = ResizeArray<string>()
                for part in parts do
                    if part = ".." then if normalized.Count > 0 then normalized.RemoveAt(normalized.Count - 1)
                    elif part <> "." then normalized.Add part
                RepositoryPath(String.Join('/', normalized))

    let rec private imagesInInline = function
        | Image(source, _, _) -> [ source ]
        | Emphasis children | Strong children | Strikethrough children -> children |> List.collect imagesInInline
        | Link(_, children) -> children |> List.collect imagesInInline
        | _ -> []

    let rec private imagesInBlock = function
        | Heading(_, xs) | Paragraph xs -> xs |> List.collect imagesInInline
        | ListItem(_, _, _, blocks) | Quote blocks | Footnote(_, blocks) -> blocks |> List.collect imagesInBlock
        | Table(header, _, rows) -> (header :: rows) |> List.collect id |> List.collect id |> List.collect imagesInInline
        | _ -> []

    let imageTargets document = document |> List.collect (fun located -> imagesInBlock located.Block) |> List.distinct

    let private soleImage = function
        | Paragraph [ Image(source, _, _) ] -> Some source
        | _ -> None

    /// A repository image can change while its Markdown reference stays identical. Promote that otherwise
    /// unchanged row to modified so unified and side-by-side views do not hide the asset change.
    let markChangedImages (images: MarkdownImageData list) (rows: RenderedMarkdownRow list) =
        let bytes side source =
            images
            |> List.tryFind (fun image -> image.Side = side && image.Source = source)
            |> Option.bind _.Bytes
        rows
        |> List.map (fun row ->
            match row.Change, row.Previous |> Option.bind (fun located -> soleImage located.Block), row.Current |> Option.bind (fun located -> soleImage located.Block) with
            | MarkdownChangeKind.Unchanged, Some oldSource, Some newSource ->
                match bytes MarkdownImageSide.Old oldSource, bytes MarkdownImageSide.New newSource with
                | Some oldBytes, Some newBytes when oldBytes <> newBytes -> { row with Change = MarkdownChangeKind.Modified }
                | _ -> row
            | _ -> row)

    /// GitHub-compatible-enough heading slug: formatting is already removed by inlineText; punctuation is
    /// discarded, spaces become hyphens, repeated hyphens are retained as GitHub does, and Unicode letters remain.
    let headingSlug (text: string) =
        if isNull text then ""
        else
            text.Trim().ToLowerInvariant()
            |> Seq.choose (fun c ->
                if Char.IsLetterOrDigit c || c = '_' || c = '-' then Some c
                elif Char.IsWhiteSpace c then Some '-'
                else None)
            |> Seq.toArray
            |> String

    let private relocate (source: string) (parent: LocatedMarkdownBlock) block =
        let lines = source.Replace("\r\n", "\n").Split '\n'
        let needle = blockText block |> fun text -> (text.Split '\n').[0].Trim()
        let first, last = max 0 (parent.FirstLine - 1), min (lines.Length - 1) (parent.LastLine - 1)
        let found =
            if String.IsNullOrWhiteSpace needle then None
            else [ first .. last ] |> List.tryFind (fun index -> lines[index].Contains(needle, StringComparison.Ordinal))
        match found with
        | Some index -> { Block = block; FirstLine = index + 1; LastLine = index + 1 }
        | None -> { parent with Block = block }

    let rec private flattenLeaf source (located: LocatedMarkdownBlock) =
        let wrap block = relocate source located block
        match located.Block with
        | Quote children ->
            children
            |> List.collect (fun child -> flattenLeaf source (wrap child) |> List.map (fun leaf -> { leaf with Block = Quote [ leaf.Block ] }))
        | Footnote(label, children) ->
            children
            |> List.collect (fun child -> flattenLeaf source (wrap child) |> List.map (fun leaf -> { leaf with Block = Footnote(label, [ leaf.Block ]) }))
        | ListItem(depth, marker, checkedState, children) ->
            let own = children |> List.filter (function ListItem _ -> false | _ -> true)
            let nested = children |> List.choose (function ListItem _ as item -> Some item | _ -> None)
            let current = if own.IsEmpty then [] else [ wrap (ListItem(depth, marker, checkedState, own)) ]
            current @ (nested |> List.collect (fun child -> flattenLeaf source (wrap child)))
        | Table(header, alignments, rows) ->
            let headerLeaf = if header.IsEmpty then [] else [ { located with Block = Table(header, alignments, []); LastLine = located.FirstLine } ]
            let body = rows |> List.mapi (fun index row ->
                let line = min located.LastLine (located.FirstLine + 2 + index)
                { Block = Table([], alignments, [ row ]); FirstLine = line; LastLine = line })
            headerLeaf @ body
        | _ -> [ located ]

    let private parseCache = ConcurrentDictionary<string, LocatedMarkdownBlock list>()
    let private documentCache = ConcurrentDictionary<string, RenderedMarkdownRow list>()
    let private diffCache = ConcurrentDictionary<string, RenderedMarkdownRow list>()
    let private cacheKey (source: string) = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes source))
    let private trimCache (cache: ConcurrentDictionary<string, 'value>) = if cache.Count >= 128 then cache.Clear()

    let parse (source: string) =
        let source = if isNull source then "" else source
        let key = cacheKey source
        parseCache.GetOrAdd(key, fun _ ->
            trimCache parseCache
            let document = Markdig.Markdown.Parse(source, pipeline)
            located source document |> List.collect (flattenLeaf source))

    /// All image work required by a rendered comparison. Resolution and revision-side semantics stay in Core;
    /// the UI receives bytes and only decodes/draws them.
    let imageRequests oldDocumentPath newDocumentPath oldSource newSource =
        let requests side documentPath source =
            parse source
            |> imageTargets
            |> List.map (fun imageSource ->
                { Side = side; Source = imageSource; Target = resolveTarget documentPath imageSource })
        requests MarkdownImageSide.Old oldDocumentPath oldSource
        @ requests MarkdownImageSide.New newDocumentPath newSource

    let renderDocument source =
        let source = if isNull source then "" else source
        documentCache.GetOrAdd(cacheKey source, fun _ ->
            trimCache documentCache
            parse source
            |> List.map (fun current ->
                { Change = MarkdownChangeKind.Unchanged; Current = Some current; Previous = Some current
                  CurrentText = blockText current.Block; PreviousText = blockText current.Block
                  Source = sourceSlice source current; PreviousSpans = blockSpans current.Block; CurrentSpans = blockSpans current.Block; Words = []; CodeLines = []; MoveCounterpartLine = None }))

    let renderDiff oldSource newSource =
        let oldSource = if isNull oldSource then "" else oldSource
        let newSource = if isNull newSource then "" else newSource
        let key = cacheKey oldSource + ":" + cacheKey newSource
        diffCache.GetOrAdd(key, fun _ ->
            trimCache diffCache
            align (parse oldSource) (parse newSource) |> projectRows oldSource newSource)
