# Network Accelerator third-party notices

- AndroidLiquidGlass, Apache License 2.0. Copyright 2025 Kyant.
  Source: https://github.com/Kyant0/AndroidLiquidGlass (kmp branch).
  The rounded-rectangle refraction in Controls/LiquidGlassBar.cs adapts the SDF gradient and circular edge profile from backdrop/internal/Shaders.kt. It is modified for composited Skia snapshots and Avalonia pointer-driven geometry. The internal selection and expanded interaction states also reference LiquidBottomTabs.kt; no Compose components or duplicated icon layers are shipped.
  Full license: AndroidLiquidGlass.LICENSE.txt.

- Remix Icon, Apache License 2.0. Copyright Remix Icon contributors.
  Source: https://github.com/Remix-Design/RemixIcon
  License: https://www.apache.org/licenses/LICENSE-2.0
  The glyphs in Themes/NetworkAcceleratorIcons.axaml are path data from Remix Icon, embedded as Avalonia stream geometries. No Remix font file is shipped.

Ant Design Icons (MIT, https://github.com/ant-design/ant-design-icons) is the approved fallback for a missing Remix glyph. This build does not embed any Ant Design icon; every glyph in the dictionary comes from Remix Icon.
