# Plan GitHub Copilot — KidsColoringApp (WPF C#) v2
# Ajout : Génération outline dynamique + Import image → outline

---

## Architecture multi-couches (Layer System)

Le moteur de rendu passe de 1 bitmap à **3 couches composites** :

```
[Couche 3] OutlineLayer     — WriteableBitmap — noir sur transparent — NON modifiable
[Couche 2] DrawingLayer     — WriteableBitmap — dessin enfant — modifiable
[Couche 1] BackgroundLayer  — WriteableBitmap — blanc fixe — jamais modifié
```

### Composite rendu (ordre)
- `BackgroundLayer` → fond blanc
- `DrawingLayer` → coups de pinceau, flood fill
- `OutlineLayer` → overlay outline noir par-dessus tout

### Règle fondamentale
- L'enfant **ne peut jamais peindre par-dessus** un pixel noir de `OutlineLayer`
- Flood fill s'arrête aux pixels noirs de `OutlineLayer` (barrière de contour)
- Eraser efface `DrawingLayer` seulement — les outlines restent intacts

### Composite final (rendu à l'écran)
- Méthode : `WriteableBitmapComposite` — fusion manuelle pixel par pixel
- Ordre : Background → Drawing → Outline (alpha blending simple)
- Format pixel : `Pbgra32` (pré-multiplié, supporte alpha)
- Recalcul du composite : uniquement sur la **dirty region** modifiée (perf)

---

## Mode 1 — Génération d'outline dynamique

### Concept
Générer des contours de formes simples (animaux, objets) directement dans l'app,
sans import externe, via des **PathGeometry WPF rendues en bitmap**.

### Pipeline de génération
```
SVG Path string / PathGeometry
    → WPF PathGeometry.Parse()
    → Rasterisation via RenderTargetBitmap (DrawingVisual + GeometryDrawing)
    → Stroke noir, Fill transparent, épaisseur ~4px
    → Copier pixel-par-pixel vers OutlineLayer (Pbgra32)
    → Centrer/scaler sur le canvas (UniformToFill dans les bounds)
```

### Source des formes
- Fichiers `.svg` embarqués en ressource (`Build Action = Resource`)
- Parser SVG minimal : extraire l'attribut `d=""` du `<path>` principal
- `PathGeometry.CreateFromGeometry(Geometry.Parse(svgPathData))`
- ⚠️ WPF ne parse pas le SVG complet — extraire uniquement les paths `d=`
- Alternative : stocker les paths XAML `<PathGeometry>` directement en ressource XAML

### Bibliothèque SVG optionnelle
- `SharpVectors` (NuGet) : rendu SVG complet vers WPF DrawingGroup
- Permet import de SVG complexes (multi-path, groupes) sans parser manuellement
- Pipeline : `SvgDrawingCanvas` → `RenderTargetBitmap` → `OutlineLayer`

### Galerie de templates
- `TemplateLibrary` : liste statique de `DrawingTemplate`
- `DrawingTemplate` : { Name, ResourceKey, ThumbnailSource }
- UI : `ListBox` avec thumbnails (60×60) — sélection lance la génération
- Templates suggérés pour 4 ans : soleil, maison, chat, étoile, fleur, poisson, ballon

### Post-traitement outline généré
- **Dilatation morphologique** : épaissir le trait si trop fin (kernel 3×3, 1-2 passes)
- **Binarisation** : tout pixel non-blanc → noir pur (255,0,0,0 → 255,0,0,0)
- Résultat : outline net, épais, visible pour petit enfant

---

## Mode 2 — Import image → conversion outline

### Pipeline complet
```
Fichier image (JPG/PNG/BMP)
    → Décodage : BitmapImage (WPF natif)
    → Redimensionnement : ajuster aux dimensions canvas (1600×1200)
    → Conversion Grayscale (luminance)
    → Flou gaussien léger (réduction bruit)
    → Détection de contours (edge detection)
    → Binarisation + nettoyage
    → Inversion + dilatation
    → OutlineLayer (noir sur transparent)
```

### Décodage et resize
- `BitmapImage` avec `DecodePixelWidth/Height` — resize natif WPF sans tiers
- Copie vers `WriteableBitmap` format `Pbgra32` via `CopyPixels` + `WritePixels`
- Scale : `Stretch.Uniform` pour conserver ratio

### Conversion Grayscale
- Formule luminance : `L = 0.299R + 0.587G + 0.114B`
- Opérer directement sur le buffer `byte[]` copié via `CopyPixels`

### Flou gaussien (pré-traitement)
- Kernel 5×5 gaussien appliqué sur l'image grayscale
- Implémentation : convolution 2D séparable (1D horizontal puis 1D vertical, perf)
- But : supprimer le bruit texture/photo avant edge detection

### Détection de contours : **Opérateur Sobel**
- Kernels Gx (horizontal) et Gy (vertical) 3×3
- Magnitude : `M = sqrt(Gx² + Gy²)` ou approximation `|Gx| + |Gy|`
- Normaliser vers 0–255
- Seuil configurable (valeur par défaut ~40–60)

```
Gx = [[-1, 0, 1], [-2, 0, 2], [-1, 0, 1]]
Gy = [[-1,-2,-1], [ 0, 0, 0], [ 1, 2, 1]]
```

### Alternative Canny (si Sobel insuffisant)
- Si l'image source est complexe (photo réelle), Sobel donne trop de bruit
- Canny = Sobel + Non-Maximum Suppression + Double Threshold + Edge Tracking
- Plus propre mais plus complexe à implémenter
- Recommandé si qualité insuffisante avec Sobel seul

### Binarisation des edges
- Pixel edge (magnitude > seuil) → noir opaque `(A=255, R=0, G=0, B=0)`
- Pixel non-edge → transparent `(A=0, R=0, G=0, B=0)`
- Résultat : `OutlineLayer` prêt pour le composite

### Dilatation morphologique (épaississement trait)
- Kernel 3×3 ou 5×5 dilation sur les pixels noirs
- But : rendre les contours plus épais et visibles pour un enfant de 4 ans
- 2–3 passes recommandées selon l'image source

### Nettoyage (débruitage post-edge)
- Supprimer les ilôts isolés (pixel noir seul sans voisin noir = bruit)
- Flood fill inverse : identifier les composantes connexes de taille < 10 pixels → effacer
- Résultat : outline propre sans parasites

### Curseur d'import (UX)
- `OpenFileDialog` filtré : `*.jpg;*.jpeg;*.png;*.bmp`
- Traitement dans un `Task.Run()` (hors UI thread — calcul lourd)
- Pendant le traitement : overlay `ProgressRing` ou `Border` semi-transparent "Chargement..."
- Résultat `OutlineLayer` transféré vers UI via `Dispatcher.InvokeAsync`

---

## Adaptation du Flood Fill pour les couches

### Problème
Le flood fill doit remplir `DrawingLayer` mais **s'arrêter aux contours** de `OutlineLayer`.

### Solution : Fill avec double contrainte
```
Condition d'expansion d'un pixel P :
  1. P dans DrawingLayer = couleur source (même que pixel clic)
  2. P dans OutlineLayer = transparent (A == 0)
  → Si OutlineLayer[P] est noir (A > 128) : STOP — c'est une barrière
```

### BFS modifié
- Lire simultanément `DrawingLayer` et `OutlineLayer` buffers
- Écrire uniquement dans `DrawingLayer`
- Composite recalculé après complétion du fill

---

## Adaptation du Brush/Eraser pour les couches

### Brush
- Écrire dans `DrawingLayer` uniquement
- **Ne pas écraser** les pixels correspondants à un outline noir dans `OutlineLayer`
- Masque : pour chaque pixel du cercle brush, skip si `OutlineLayer[pixel].A > 128`

### Eraser
- Écrire blanc dans `DrawingLayer` uniquement
- Les outlines de `OutlineLayer` restent intacts

---

## Undo — adaptation couches

- Undo sauvegarde uniquement `DrawingLayer` (pas OutlineLayer — jamais modifié par l'enfant)
- `CanvasHistory` : `Stack<byte[]>` (buffer raw copié via `CopyPixels`)
- Restauration : `WritePixels` vers `DrawingLayer` + recalcul composite

---

## AppState — mise à jour

```
AppState
├── ActiveColor         : Color
├── ActiveTool          : DrawingTool { Brush, Eraser, Fill, Clear, ImportImage, SelectTemplate }
├── BrushSize           : double
├── CanvasHistory       : Stack<byte[]>          ← DrawingLayer snapshots uniquement
├── BackgroundLayer     : WriteableBitmap         ← blanc fixe, init une fois
├── DrawingLayer        : WriteableBitmap         ← dessin enfant
├── OutlineLayer        : WriteableBitmap         ← outline noir, transparent bg
├── CompositeOutput     : WriteableBitmap         ← rendu final affiché
├── HasOutline          : bool                    ← true si outline chargé
└── IsProcessing        : bool                    ← true pendant import/traitement
```

---

## Services — mise à jour

```
Services/
├── BitmapDrawingService.cs     ← brush, eraser, fill (couche-aware), clear, undo
├── CompositeRenderService.cs   ← fusion 3 couches → CompositeOutput (dirty region)
├── OutlineGeneratorService.cs  ← Mode 1 : SVG/Path → OutlineLayer
├── ImageToOutlineService.cs    ← Mode 2 : import image → pipeline edge detection
│   ├── Grayscale()
│   ├── GaussianBlur()
│   ├── SobelEdgeDetect()
│   ├── Binarize()
│   ├── Dilate()
│   └── Denoise()
└── SoundService.cs
```

---

## UI — ajouts

### Bouton "Choisir dessin" (Mode 1)
- Ouvre un `Popup` ou `Flyout` avec la galerie de templates (thumbnails)
- Sélection remplace `OutlineLayer` + remet `DrawingLayer` à blanc

### Bouton "Importer image" (Mode 2)
- Lance `OpenFileDialog`
- Lance le pipeline async `ImageToOutlineService`
- Affiche indicateur de chargement
- Résultat → `OutlineLayer` + remet `DrawingLayer` à blanc

### Slider épaisseur outline (optionnel, UI parent)
- Contrôle le nombre de passes de dilatation morphologique
- Valeurs : 1 (fin) à 5 (très épais)
- Masqué par défaut — accessible via coin discret pour le parent

---

## NuGet requis — mise à jour

| Package | Usage |
|---|---|
| `CommunityToolkit.Mvvm` | MVVM, RelayCommand, ObservableObject |
| `SharpVectors.Reloaded` | Rendu SVG complet vers WPF (Mode 1, si SVG complexes) |
| Aucun autre | Edge detection, composite, morphologie = implémentation manuelle sur buffer |

> ⚠️ Éviter `OpenCvSharp` ou `ImageSharp` pour ce projet — overkill, lourd.
> Tout le pipeline image peut être fait sur des `byte[]` bruts avec des boucles optimisées.

---

## Priorité de développement (ordre révisé)

1. Système 3 couches + composite render (base de tout)
2. Brush/Eraser/Fill adaptés aux couches
3. Undo sur DrawingLayer seulement
4. **Mode 1** : 3-4 templates SVG simples → OutlineLayer
5. **Mode 2** : import image → grayscale → Sobel → binarize → dilatation
6. Intégration UI (galerie templates + bouton import)
7. Slider épaisseur outline (parent)
8. Sons + polish UI enfant
