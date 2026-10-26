# Caladesi brand assets

The mark is a two-tone C (sky over water) with a setting sun beside it.
The SVGs in `svg/` are the masters; every PNG and the favicon are rendered from them.

| File | Use |
|---|---|
| `svg/icon-dark.svg` | Primary icon (rounded tile). Default everywhere an icon is needed. |
| `svg/icon-light.svg` | Icon on a sand tile, for dark surroundings where the dark tile disappears. |
| `svg/avatar.svg` | Full-bleed square for GitHub/org avatars (the site crops it to a circle). |
| `svg/mark-on-light.svg`, `svg/mark-on-dark.svg` | The mark without a tile, transparent background. Also the `.cala` file icon. |
| `svg/wordmark-on-light.svg`, `svg/wordmark-on-dark.svg` | The name spelled out; the C is the mark and the sun is the dot on the i. |
| `png/` | Rendered sizes: icon 16–1024, NuGet `nuget-icon-128.png`, avatar, marks, wordmarks at 2000 px wide. |
| `favicon.ico` | 16/32/48 px. |

Text in the wordmark SVGs is converted to outlines, so no font is needed to display them.

## Colors

| Name | Hex | Role |
|---|---|---|
| Gulf | `#0B3A47` | Tiles, dark backgrounds, text on light |
| Sand | `#F1E6CF` | Upper arc on dark, text on dark |
| Sea | `#1FA39A` | Lower arc on dark |
| Sea (deep) | `#12808A` | Lower arc on light |
| Sun | `#F2884B` | The sun dot, always |

## Type

Outfit SemiBold (600), letter-spacing -0.015em — [Google Fonts](https://fonts.google.com/specimen/Outfit), SIL Open Font License.
