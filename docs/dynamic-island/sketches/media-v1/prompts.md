# Media v1 — промпт сравнительного листа

Встроенный image_gen, transparent_background=false. Reference: ../astra-v3/island-storyboard-v3.png (материал, тело, лента и desktop-контекст; не содержимое виджетов). Никаких запросов медиасессии или сервисов не выполнялось.

```text
Use case: ui-mockup. Generate ONE high-quality comparative storyboard with THREE compact media-player variants, all living INSIDE the same approved solid black Windows Dynamic Island. This is a STATIC INTERACTION STUDY, not implemented software. Use the supplied v3 storyboard only as the reference for black material, continuous top-edge body, persistent internal navigation, Windows desktop scale, subdued sea-at-dusk background and Explorer corner. Do NOT reproduce its audio or network widget, four rows or cursor-pull demo.

CANVAS: approximately1536x1408portrait-ish, exactly THREE VERY WIDE horizontal desktop strips stacked vertically, identical relative scale. Each strip shows the same upper portion of a broad Windows desktop. Slim neutral off-white title bands OUTSIDE UI:
"A / КОЛЬЦО + ШКАЛА ПО ЗАПРОСУ"
"B / КОЛЬЦО ДЛЯ ПЕРЕМОТКИ"
"C / ЛИНЕЙНАЯ ШКАЛА"
Each desktop crop has a quiet blue-gray seascape wallpaper and the same small upper corner of Windows File Explorer in the lower far right. The island is top-center, attached flush to the desktop's top edge. It occupies only about40%of desktop width, not the entire screen. Use enough height for readable14–16px UI type. Compact adult software, crisp geometry, no colorful visualizer or poster design.

COMMON ISLAND in all3rows: one continuous solid-black body, softly rounded lower corners, a thin organic lower contour, restrained shadow. The internal navigation row is ALWAYS VISIBLE at same location, INSIDE the same body. TEN small monochrome glyphs, same order and spacing: music note(active with gray rounded selection pill), screenshot frame, trash, charging plug, kanban columns, Wi-Fi, speaker, battery, RU, bell. No separate toolbar, no detached card, no popover, no Back, no clock, no overflow menu. The black shape itself contains all the media content. Plain neutral white/gray controls and labels, no glass/cyan/neon/large colored fields.

COMMON MEDIA LAYOUT, IDENTICAL in all3variants:
- A neat small metadata line just under navigation: left aligned track title"Двери вагона" in16px, artist"Fluxyluv"in13pxbelow; right aligned source"Яндекс Музыка"in11–12pxgray. Source is a plain label, NOT a dropdown, NO chevron, no service logo.
- Below metadata, centered horizontally: PREVIOUS TRACK glyph on LEFT, a COMPACT circular64pxALBUM COVER in CENTER, NEXT TRACK glyph on RIGHT. Prev/next about70pxfromcovercenter, ordinary white skip icons, neither oversized nor boxed in big circles.
- Album artwork is the SAME small neutral abstract composition of charcoal, warm stone and pale geometric paper shapes in all3rows. No famous artwork, no photo, no text, no bright gradient. THE COVER ITSELF IS THE PLAY/PAUSE BUTTON. Put a modest white PAUSE symbol OVER THE CENTER OF THE COVER so its action is visible. No separate play/pause button anywhere below, beside or above the cover. Never replace cover with an empty play circle.
- One reserved96pxsquare region around cover so variants share exact cover size and control positions.
- DEMO timing in all rows: current1:18, total3:42, approximately35%progress. Never label this live playback. No duration or data has been read from an application.
- All timings and interaction previews stay INSIDE the enclosing black island. Explanatory diagrams/labels are OUTSIDE the island on its LEFT, small and restrained.

A — INFORMATIONAL RING + REVEALED SEEKER:
Around the64pxcover draw a thin2pxwhite progress arc at35%on a faint neutral ring, with clear air gap fromcover. This ring is PASSIVE: no thumb, no cursor, no draggable band. The central pause action remains clear.
At the bottom INSIDE this same body show a small buttonlabel"Перемотка" and its already-REVEALED compact horizontal seek track with a white thumb at35%, current"1:18"atleft and total"3:42"atright. This is the expanded-on-request state; it is NOT a separate floating panel. Same black outer shape encloses the added row. A tiny outside annotation"Шкала раскрыта"may point to this area.
LEFTSIDE EXPLANATORY DIAGRAM outside island: a small outline circle with shaded center and thin outerarc; two short labels"Центр: play/pause"and"Кольцо: прогресс". A tiny downward arrow from a text cue"Перемотка"to a straight line demonstrates the request-only seeker. No long paragraph.

B — OUTER DRAG RING, CENTER PLAY/PAUSE:
Same64pxcover with pause overlay. Around it a visibly SEPARATE external annular interaction band about12pxthick, outside a4–6pxdark gap. The ring's outerdiameter is approximately96px. Neutral darkgray band, thin white progressarc at35%, small white draghandle, not a white donut. The interactive arc is almost circular but has a small deliberate GAP around12o'clock, so start andend are distinct. No connecting stroke across this gap.
Visible white cursor on OUTER band near7o'clock with a small timepreview"2:06" in a modest dark rectangularlabel NEAR THE RING BUT STILL INSIDE THE MAIN BODY. Preview does not cover the cover or pauseglyph. Show current timing"1:18 / 3:42"belowcomposition. No linear seeker inB.
LEFTSIDE EXPLANATORY DIAGRAM outside island: an enlarged but compact concentric-zone diagram with central disk labelled"Клик: play/pause", outerband labelled"Тянуть: перемотка". A curved arrow follows ONLY the ring, never crosses center. Point to topgap with tiny"Начало / конец". A short lowernote"Превью 2:06"may explain the hover. Keep this a clean technical illustration, NOT giant UI ring.

C — ALWAYS-AVAILABLE LINEAR SEEKER:
Same64pxcover with pauseoverlay and prev/next, but NO RING of any kind around it. Below thecompactplaybackcomposition, INSIDE SAME BLACK BODY, a thin horizontal progressslider about260pxlong with white thumb at35%;"1:18"and"3:42"atitsends. Small cursor near slider with subtle preview"2:06"above track but insidebody. No secondplay/pause button.
LEFTSIDE EXPLANATORY DIAGRAM outsideisland: centralclickdisk and separatehorizontaltrack withstraightleft-rightarrow. Labels"Обложка: play/pause"and"Шкала: перемотка". One short note"Прямой доступ".

CRITICAL FAIR-COMPARISON RULES: all3 islands have samewidth, same navigation, same track/source typography, samecoverimage and64pxcover size, sameprev/nextplacement. Only seek/progressmechanics differ. A may become about24pxtaller because itsseeker is expanded; do not letAbecome a settingspanel. No decorative equalizer, playlist, shuffle, repeat, service selector, new autoplayevents, albumrecommendations, dropdowns, extra integration. No copiedlegacy popup pastedontoisland. ONE unifiedblackshapeperrow. Keep Russian text accurate and diagrams short. Footer outsideallUI:"Три гипотезы взаимодействия · кольцо ещё не выбрано".
```

