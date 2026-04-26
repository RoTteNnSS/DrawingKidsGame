# Plan GitHub Copilot — KidsColoringApp (WPF C#)

## Contexte du projet
Application de dessin/coloriage destinée à un enfant de 4 ans.
But principal : introduction à la souris et à l'ordinateur.
Interface ultra-simplifiée, feedback visuel immédiat, aucun texte dans l'UI.

---

## Architecture générale

- Pattern : **MVVM** (CommunityToolkit.Mvvm)
- Un seul `Window` (pas de navigation, pas de menus)
- Deux zones distinctes :
  - `ToolbarView` (gauche ou bas) — palette + outils
  - `CanvasView` (centre) — zone de dessin principale
- Pas de File/Save/Open — session éphémère intentionnelle

---

## Modèle de données (Domain)

### `AppState` (ViewModel racine)
- `ActiveColor` : `Color` — couleur sélectionnée
- `ActiveTool` : enum `DrawingTool { Brush, Eraser, Fill, Clear }`
- `BrushSize` : `double` — taille fixe, grande (min 20px)
- `CanvasHistory` : `Stack<BitmapSource>` — pour undo (max 10 états)

### `ColorPalette`
- Liste statique de 12 couleurs `Color[]`
- Couleurs vives uniquement (HSV saturé, luminosité haute)
- Pas de color picker — jamais

---

## Couche Canvas (Vue + Code-behind)

### Technologie de rendu : `WriteableBitmap`
- Résolution fixe (ex. 1600×1200) indépendante du DPI écran
- Fond blanc au démarrage
- Rendu pixel-level via `WriteableBitmap.Lock()` / `AddDirtyRect()`

### Gestion souris
- Événements WPF natifs : `MouseDown`, `MouseMove`, `MouseUp`
- Capturer la souris (`Mouse.Capture`) pour tracer hors bounds
- Interpolation linéaire entre `lastPoint` et `currentPoint` (évite les trous à vitesse rapide)
- Coordonnées : transformer de `UIElement` vers coordonnées bitmap (`ScaleTransform` inversée)

### Outil Brush
- Algorithme : **Midpoint circle / filled circle** centré sur chaque point interpolé
- Écriture directe dans le buffer `WriteableBitmap` (unsafe ou `Marshal.Copy`)
- Couleur = `AppState.ActiveColor`, taille = `AppState.BrushSize`

### Outil Eraser
- Identique au brush mais couleur = Blanc fixe
- Taille légèrement plus grande que le brush par défaut

### Outil Fill (Flood Fill)
- Algorithme : **BFS itératif** (pas récursif — stack overflow sur grande image)
- File d'attente : `Queue<Point>` 
- Condition d'arrêt : pixel de même couleur que le pixel source (tolérance couleur optionnelle ±10)
- Opère sur une copie du buffer, puis écrit le résultat en une passe (`WriteableBitmap`)
- Exécuté sur le **UI Thread** (pas de Task/async — éviter les bugs de synchronisation bitmap)

### Undo
- Avant chaque coup de pinceau (MouseDown) : sauvegarder un snapshot `CopyPixels` → `WriteableBitmap` clonée dans `CanvasHistory`
- Bouton Undo : `Pop()` la pile, écrire dans le bitmap courant via `WritePixels`
- Limite : 10 niveaux max (mémoire)

### Clear All
- Remplir le buffer en blanc (memset via `Marshal.Copy` ou boucle `unsafe`)
- Ajouter à `CanvasHistory` avant clear

---

## Couche UI (XAML)

### Palette couleurs
- `UniformGrid` 3×4 ou `WrapPanel`
- Chaque bouton : `Rectangle` 60×60px, `CornerRadius` arrondi, pas de texte
- Sélection : bordure blanche épaisse + légère animation `ScaleTransform` (110%)
- Binding : `Command` vers `SelectColorCommand(Color)`

### Boutons outils
- 4 boutons : Brush, Eraser, Fill, Clear
- Icône SVG ou `Path` XAML uniquement — pas de texte
- Taille minimale : 70×70px
- Style visuel actif : `ToggleButton` ou `Border` mise en évidence

### Canvas
- `Image` WPF bindé à `WriteableBitmap` via `Source`
- `Stretch="Uniform"` pour remplir l'espace disponible
- `RenderOptions.BitmapScalingMode` = `NearestNeighbor` (rendu net, pas flou)
- Overlay curseur : `Ellipse` transparente suivant la souris (taille = BrushSize en unités écran)

### Curseur personnalisé
- Cacher curseur système dans la zone canvas (`Cursor="None"`)
- Afficher une `Ellipse` WPF colorée qui suit `MouseMove`
- Transformée de coordonnées canvas→écran pour positionner l'ellipse

---

## Feedback enfant

### Son
- `System.Windows.Media.MediaPlayer` ou `SoundPlayer`
- Son léger au début du tracé (`MouseDown`)
- Son distinct pour Fill (satisfaisant, "plouf")
- Son pour Clear (drôle)
- Fichiers `.wav` intégrés en ressource (`Build Action = Resource`)

### Animation curseur
- L'ellipse-curseur pulse légèrement quand `MouseDown` (`DoubleAnimation` sur `Scale`, 100ms)

---

## Structure projet recommandée

```
KidsColoringApp/
├── .github/
│   └── copilot-instructions.md   ← ce fichier résumé
├── App.xaml
├── MainWindow.xaml
├── ViewModels/
│   ├── MainViewModel.cs           ← AppState + commands
│   └── ColorPaletteViewModel.cs
├── Views/
│   ├── CanvasView.xaml            ← Image + mouse events (code-behind autorisé)
│   └── ToolbarView.xaml           ← palette + outils
├── Models/
│   └── DrawingTool.cs             ← enum
├── Services/
│   ├── BitmapDrawingService.cs    ← brush, eraser, fill, clear, undo
│   └── SoundService.cs            ← lecture sons
├── Resources/
│   ├── Sounds/
│   │   ├── brush.wav
│   │   ├── fill.wav
│   │   └── clear.wav
│   └── Icons/                     ← Path XAML pour les outils
└── Styles/
    └── ChildUI.xaml               ← styles globaux gros boutons, polices
```

---

## Contraintes importantes pour Copilot

- **Pas de texte dans l'UI** — uniquement icônes et couleurs
- **Pas de dialog, popup, message box** — jamais
- **Boutons minimum 60×60px** partout
- **WriteableBitmap** est le seul mode de rendu — pas d'InkCanvas, pas de Canvas WPF shapes
- **Flood fill = BFS itératif** — jamais récursif
- **Mouse capture** obligatoire sur MouseDown
- **Interpolation linéaire** obligatoire entre deux points souris (Bresenham ou lerp simple)
- **Undo max 10 niveaux** — au-delà, `Dequeue` le plus ancien
- **Pas d'async/await** dans le pipeline de dessin — synchrone uniquement
- **Sons = ressources embarquées** — pas de chemins absolus
- **Palette couleur = constante statique** — pas de fichier de config

---

## NuGet requis

| Package | Usage |
|---|---|
| `CommunityToolkit.Mvvm` | MVVM, RelayCommand, ObservableObject |
| Aucun autre requis | WriteableBitmap, MediaPlayer = natif WPF |

---

## Priorité de développement (ordre)

1. `WriteableBitmap` affiché + souris trace un trait (brush basique)
2. Palette couleurs fonctionnelle
3. Eraser
4. Flood fill BFS
5. Undo (10 niveaux)
6. Clear
7. Overlay curseur animé
8. Sons
9. Polish UI (tailles, arrondis, animations sélection)
