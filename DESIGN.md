# SnagItOpen visual system

The approved upgrade PRD, sections 4-7, governs the design. This file is its concise working index.

## Theme and color
Use Themes/Tokens.Light.xaml, Tokens.Dark.xaml and Tokens.HighContrast.xaml through DynamicResource or SetResourceReference. Follow Windows by default; high contrast wins. Quiet neutral surfaces, blue selection chrome, brand red for the primary Copy action and logo. Annotation colors remain content. Preserve the PRD's exact color values.

## Typography and metrics
Segoe UI Variable Text with Segoe UI fallback. Caption 11, body 12, subtitle 14 semibold, title 20 semibold. Spacing 2/4/8/12/16/24/32 DIP, controls 28 DIP, rail buttons 36 DIP, control radius 4, card/toast radius 8. Use Themes/Metrics.xaml.

## Layout
Classic menu, 44 DIP command bar, 48 DIP left tool rail, Layers panel, central canvas with conditional InfoBars and lower-right toast stack, contextual inspector, recent captures strip and 28 DIP status bar. Classic tools toolbar is optional. Below 1100 DIP collapse Layers; below 860 use a Properties flyout. Keep at least 320 DIP canvas.

## Components and interaction
Use native WPF shared controls, original 16x16 monoline geometries, keyboard-only focus rings and descriptive automation names. One home per visible control. Contextual privacy notices. Immediate-release capture remains default; Adjust mode is optional. Reduced motion honors Windows. Never recolor document pixels or change export behavior for a theme.
