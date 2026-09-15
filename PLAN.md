# GitKay Plan

## Vision
GitKay is a modern, high-performance Git history visualizer designed as a lean and fast alternative to `gitk`. It focuses on a streamlined subset of features, providing a responsive and aesthetically pleasing experience for viewing commit history and performing common branch/tag operations.

## The Core Requirements

- **Three-Pane Layout**:
    - **Top (Tree/Graph)**: A visual representation of the git commit graph and history.
    - **Middle (Commit Info)**: Details about the selected commit (message, author, hash, etc.).
    - **Bottom (Diff View)**: Detailed changes introduced by the commit.
- **Modern UI/UX**:
    - Fast startup and smooth navigation.
    - Minimalist menu structure (primarily "Reread Refs").
    - Modern aesthetics suitable for contemporary development environments.
- **Workflow Integration**:
    - Contextual actions available directly from the commit graph.
    - Integrated blame functionality within the diff view.

## Core Functionality
### Navigation & Visualization
- Visual commit graph showing branches and merges.
- Efficient "Reread Refs" to refresh the state from the underlying repository.

### Commit Actions (Context Menu)
- Create tag at selected commit.
- Create branch at selected commit.
- Cherry-pick selected commit.
- Reset current branch to selected commit.
- Revert selected commit.

### Diff & Blame
- High-performance diff visualization.
- Inline "blame" information within the diff view for rapid history lookup.

## Tech Stack
- **Environment Management**: `mise`
- **Runtime**: .NET 10.0
- **Solution Format**: `.slnx`
- **UI Framework**: Avalonia v12.0 (C#/AXAML for UI).
- **Core Logic**: F# with Elmish-style architecture.
- **State & Flow**: 
    - `Axial`: For managing application flows.
    - `ElmishGlue`: For bridging Elmish F# logic with Avalonia UI (located at `~/projects/Elmish.Avalonia.Glue/main`).
- **Testing**: xUnit + Unquote (F#).
- **Git Backend**: High-performance Git CLI integration.

## Project Structure
- `src/`: Application projects (C# UI and F# Core).
- `tests/`: Test projects.
- `dev-docs/`: Internal documentation for developers and AI agents.
- `docs/`: User-facing documentation.
- `artifacts/`: Build outputs (configured via `Directory.Build.props`).
