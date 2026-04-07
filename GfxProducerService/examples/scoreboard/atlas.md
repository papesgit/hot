# Scoreboard Atlas

This atlas defines a single region for the scoreboard display.

## Atlas size

The scoreboard uses viewport-relative sizing (`vw` units) and `display: inline-flex`, so it scales automatically with the CEF renderer resolution.

To change the scoreboard resolution, simply change the CEF offscreen renderer size. The proportions remain constant.

Approximate dimensions as percentage of viewport width:
- Width: 100vw
- Height: ~29.6vw (header 5.12vw + col headers 2.98vw + 5 rows × 4.29vw)

Example pixel sizes at different renderer widths:
| Renderer Width | Scoreboard Width | Scoreboard Height |
| --- | --- | --- |
| 820px | ~820px | ~278px |
| 1640px | ~1640px | ~556px |
| 1920px | ~1920px | ~651px |

## Region mapping

| Region | U0 | V0 | U1 | V1 |
| --- | --- | --- | --- | --- |
| scoreboard | 0 | 0 | 1 | 1 |

