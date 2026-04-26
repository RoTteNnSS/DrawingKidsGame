# DrawingKidsGame

A kid-friendly WPF coloring application for Windows, built with .NET 10.  
Children can paint pre-made outlines, import their own images, and export their artwork.

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE.txt)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078d7)](https://www.microsoft.com/windows)

---

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Getting Started](#getting-started)
- [Usage](#usage)
- [Project Structure](#project-structure)
- [Architecture](#architecture)
- [Contributing](#contributing)
- [License](#license)

---

## Features

- 🎨 **Three drawing tools** — Brush, Eraser, Flood Fill
- 🖼️ **Import images** — PNG, JPEG, BMP, SVG; auto-converted to a black outline
- 📋 **Built-in drawing templates** — ready-to-color outline library
- 🔍 **Zoom** — zoom in / out / reset with toolbar buttons
- ↩️ **Undo** — up to 10 undo levels
- 🎨 **Custom color picker** — HSV wheel + RGB sliders + hex input
- 💾 **Export** — save as PNG, JPEG, BMP or SVG; print directly
- 🔊 **Sound effects** — audio feedback for brush, fill and clear actions
- 🖥️ **Fullscreen mode** — F12 toggle; confined to the current monitor on multi-monitor setups
- **Adaptive outline pipeline**:
  - Transparent PNG → alpha-channel extraction
  - White-background outline → Otsu thresholding
  - Color photo → Canny-like edge detection (Sobel + NMS + hysteresis + morphological closing)

---

## Requirements

| Component | Version |
|-----------|---------|
| Windows   | 10 or later (64-bit) |
| .NET SDK  | 10.0 |
| Visual Studio | 2022/2026 with **Desktop development with .NET** workload |

---

## Getting Started

```bash
# Clone the repository
git clone https://github.com/RoTteNnSS/DrawingKidsGame.git
cd DrawingKidsGame/ColorKids

# Restore and build
dotnet build DrawingKidsGame.csproj

# Run
dotnet run --project DrawingKidsGame.csproj
```

Or open `DrawingKidsGame.slnx` in Visual Studio and press **F5**.

---

## Usage

| Action | How |
|--------|-----|
| Select tool | Toolbar buttons (Brush / Eraser / Fill) |
| Change color | Click a palette swatch or **+ Custom color** |
| Adjust brush size | Drag the **Size** slider |
| Undo | Toolbar **Undo** button |
| Clear canvas | Toolbar **Clear** button |
| Load a template | Toolbar **Drawings** → pick a template |
| Import image | Toolbar **Import** → choose a file |
| Save / Export | **File** menu → PNG / JPEG / BMP / SVG |
| Print | **File → Print** or `Ctrl+P` |
| Toggle fullscreen | `F12` |
| Zoom | Toolbar `−` / `100%` / `+` buttons |

---

## Project Structure

```
DrawingKidsGame/
├── Models/
│   ├── DrawingTemplate.cs          # Template metadata
│   └── DrawingTool.cs              # Tool enum (Brush, Eraser, Fill)
├── Services/
│   ├── BitmapDrawingService.cs     # Low-level pixel drawing (stroke, flood fill)
│   ├── CompositeRenderService.cs   # Layer compositing (background + drawing + outline)
│   ├── ExportService.cs            # PNG / JPEG / BMP / SVG / Print export
│   ├── ImageToOutlineService.cs    # Adaptive image-to-outline pipeline
│   ├── OutlineGeneratorService.cs  # Outline template loading
│   ├── SoundService.cs             # WAV playback
│   └── SvgImportService.cs         # SVG import via SharpVectors
├── ViewModels/
│   └── MainViewModel.cs            # Application state (MVVM, CommunityToolkit)
├── Views/
│   ├── CanvasView.xaml(.cs)        # Drawing canvas with mouse input
│   ├── ColorPickerWindow.xaml(.cs) # HSV + RGB + Hex color picker
│   └── ToolbarView.xaml(.cs)       # Left toolbar
├── Styles/
│   └── ChildUI.xaml                # Shared WPF styles
├── Resources/
│   └── Sounds/                     # brush.wav, fill.wav, clear.wav
├── MainWindow.xaml(.cs)            # Shell window, fullscreen logic
└── DrawingKidsGame.csproj
```

---

## Architecture

The rendering pipeline uses **three `WriteableBitmap` layers** composited on every stroke:

```
BackgroundLayer   (solid color fill)
DrawingLayer      (user paint strokes)
OutlineLayer      (imported / template outline — always on top)
        ↓
CompositeRenderService  →  CompositeOutput  →  displayed Image
```

Key design points:
- **Dirty-region rendering** — only the bounding box of a stroke is re-composited, keeping large canvases fast.
- **BitArray flood fill** — visited pixels use `BitArray` (8× less memory than `bool[]`).
- **Static service classes** — no unnecessary abstractions; all bitmap operations are pure static methods.
- **CommunityToolkit.Mvvm** — `ObservableObject` + `RelayCommand` for clean MVVM without boilerplate.

---

## Contributing

See [CONTRIBUTING.md](.github/CONTRIBUTING.md) for guidelines.

---

## License

This project is licensed under the **GNU General Public License v3.0**.  
See [LICENSE.txt](LICENSE.txt) for the full text.

```
DrawingKidsGame — A kid-friendly WPF coloring application
Copyright (C) 2024  RoTteNnSS

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
```