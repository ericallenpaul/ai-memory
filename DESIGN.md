---
name: AIBrain Intelligence System
colors:
  surface: '#f7f9fb'
  surface-dim: '#d8dadc'
  surface-bright: '#f7f9fb'
  surface-container-lowest: '#ffffff'
  surface-container-low: '#f2f4f6'
  surface-container: '#eceef0'
  surface-container-high: '#e6e8ea'
  surface-container-highest: '#e0e3e5'
  on-surface: '#191c1e'
  on-surface-variant: '#434655'
  inverse-surface: '#2d3133'
  inverse-on-surface: '#eff1f3'
  outline: '#737686'
  outline-variant: '#c3c6d7'
  surface-tint: '#0053db'
  primary: '#004ac6'
  on-primary: '#ffffff'
  primary-container: '#2563eb'
  on-primary-container: '#eeefff'
  inverse-primary: '#b4c5ff'
  secondary: '#565e74'
  on-secondary: '#ffffff'
  secondary-container: '#dae2fd'
  on-secondary-container: '#5c647a'
  tertiary: '#46566c'
  on-tertiary: '#ffffff'
  tertiary-container: '#5e6e85'
  on-tertiary-container: '#e9f0ff'
  error: '#ba1a1a'
  on-error: '#ffffff'
  error-container: '#ffdad6'
  on-error-container: '#93000a'
  primary-fixed: '#dbe1ff'
  primary-fixed-dim: '#b4c5ff'
  on-primary-fixed: '#00174b'
  on-primary-fixed-variant: '#003ea8'
  secondary-fixed: '#dae2fd'
  secondary-fixed-dim: '#bec6e0'
  on-secondary-fixed: '#131b2e'
  on-secondary-fixed-variant: '#3f465c'
  tertiary-fixed: '#d3e4fe'
  tertiary-fixed-dim: '#b7c8e1'
  on-tertiary-fixed: '#0b1c30'
  on-tertiary-fixed-variant: '#38485d'
  background: '#f7f9fb'
  on-background: '#191c1e'
  surface-variant: '#e0e3e5'
typography:
  headline-xl:
    fontFamily: Inter
    fontSize: 36px
    fontWeight: '700'
    lineHeight: 44px
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Inter
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.01em
  headline-lg-mobile:
    fontFamily: Inter
    fontSize: 20px
    fontWeight: '600'
    lineHeight: 28px
  headline-md:
    fontFamily: Inter
    fontSize: 18px
    fontWeight: '600'
    lineHeight: 24px
  body-lg:
    fontFamily: Inter
    fontSize: 16px
    fontWeight: '400'
    lineHeight: 24px
  body-md:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 20px
  body-sm:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 18px
  label-md:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '600'
    lineHeight: 16px
    letterSpacing: 0.05em
  label-sm:
    fontFamily: Inter
    fontSize: 11px
    fontWeight: '500'
    lineHeight: 14px
rounded:
  sm: 0.25rem
  DEFAULT: 0.5rem
  md: 0.75rem
  lg: 1rem
  xl: 1.5rem
  full: 9999px
spacing:
  base: 8px
  container-margin: 32px
  gutter: 24px
  sidebar-width: 260px
  card-padding: 24px
  stack-sm: 8px
  stack-md: 16px
  stack-lg: 24px
---

## Brand & Style

The brand personality is analytical, reliable, and visionary. It aims to evoke a sense of precision and effortless intelligence, positioning the product as a high-performance tool for data-driven decision-making. 

The design system adopts a **Corporate / Modern** style. It prioritizes clarity and high data density through a systematic approach to whitespace and functional aesthetics. The visual language is defined by structured layouts, a crisp "white-on-gray" card architecture, and a restrained use of vibrant color to direct user attention to critical insights.

## Colors

The color palette is anchored by **Deep Navy (#0F172A)** for structural elements and primary headings, providing a sophisticated and stable foundation. **Vibrant Blue (#2563EB)** serves as the primary action color, used for buttons, active states, and data highlights.

**Slate Gray (#64748B)** is utilized for secondary text and icons to maintain a clear hierarchy without visual clutter. The background environment uses a layered neutral approach: a base of **Slate White (#F8FAFC)** with pure white (#FFFFFF) surfaces to create a subtle but distinct "card" effect. Success, warning, and error states should follow standard functional patterns using emerald, amber, and rose hues respectively, but heavily desaturated to match the professional tone.

## Typography

This design system uses **Inter** exclusively to ensure maximum legibility across dense data tables and complex dashboards. The type hierarchy relies on weight and color rather than drastic size shifts to maintain a compact, professional feel.

- **Headlines:** Use Bold (700) or Semi-Bold (600) for section titles. Apply a slight negative letter-spacing on larger sizes to create a more "locked-in" editorial appearance.
- **Body:** Standardized at 14px for most UI contexts to balance readability with information density.
- **Labels:** Small caps or uppercase tracking are used for "over-line" labels (e.g., above metric numbers) to clearly categorize data without competing with the values themselves.

## Layout & Spacing

The layout utilizes a **Fixed-Fluid Hybrid Grid**. A fixed-width sidebar (260px) persists on the left, while the main content area uses a 12-column fluid grid that scales to the browser width.

- **Breakpoints:** Mobile (<768px) collapses the sidebar into a drawer and switches the content margin to 16px. Tablet (768px - 1280px) uses a 24px margin. Desktop (>1280px) maximizes at a 32px margin.
- **Rhythm:** An 8px base-unit controls all spatial relationships. Card internal padding is set to 24px (3 units) to provide enough "breathing room" for data points.
- **Alignment:** All dashboard cards should align to the top of the grid, using the 24px gutter to maintain a clean vertical and horizontal rhythm.

## Elevation & Depth

This design system avoids heavy shadows, instead using **Low-Contrast Outlines** and **Tonal Layers** to create depth.

- **Level 0 (Background):** Slate White (#F8FAFC). Used for the overall canvas.
- **Level 1 (Surface):** Pure White (#FFFFFF). Used for cards and containers. These surfaces feature a subtle 1px border in a light gray (#E2E8F0).
- **Level 2 (Interaction):** When a card or element is hovered, a very soft, diffused shadow (0px 4px 12px rgba(15, 23, 42, 0.05)) may be applied to indicate interactivity.
- **Sidebar:** Uses a subtle right-border or a slightly darker background shade (#F1F5F9) to distinguish the navigation plane from the workspace.

## Shapes

The shape language is characterized by **Soft Geometric** forms. The primary corner radius is set to **12px (rounded-lg)** for standard dashboard cards and primary containers, striking a balance between modern friendliness and professional structure.

- **Small Components:** Buttons and input fields use a **8px (rounded-md)** radius for a crisper look.
- **Utility Elements:** Tags, badges, and status indicators use a **full pill (rounded-full)** radius to differentiate them from actionable buttons.
- **Selection States:** Navigation highlights in the sidebar use a 6px radius to fit within the narrower vertical constraints.

## Components

### Buttons
- **Primary:** Solid Vibrant Blue with white text. 8px radius.
- **Secondary:** White background with a Slate Gray border and Deep Navy text.
- **Ghost:** No background or border; used for low-priority actions in tables.

### Cards
Cards are the primary organizational unit. They must feature a pure white background, a 1px #E2E8F0 border, and 12px-16px corner radius. Padding remains consistent at 24px. Header sections within cards should have a subtle bottom border or 16px of separation from content.

### Input Fields
Inputs use a 1px border (#CBD5E1) and 8px radius. On focus, the border shifts to the Primary Blue with a 2px outer glow (ring) of the same color at 20% opacity.

### Data Tables
Tables should be borderless between columns, using only light horizontal dividers (#F1F5F9). Row heights are generous (52px+) to ensure legibility. The header row should use the `label-md` typography style in Slate Gray.

### Status Badges
Small, pill-shaped indicators. Use desaturated background tints (e.g., light green background with dark green text) to ensure they are readable but do not distract from primary dashboard metrics.