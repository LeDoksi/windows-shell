# Astra v3 — точные промпты image_gen

Built-in image_gen, transparent_background=false во всех трёх вызовах. CLI не использовался. Это история генерации, не продуктовая спецификация. После генерации пользователь уточнил: расширение допустимо вниз и в стороны ради читаемости; требования равной ширины в промпте 3 были авторским приёмом для конкретного листа и не ограничивают продукт. Пиксельное совпадение координат не утверждено пользователем.

## 1. Исходный лист

Input: ../astra-v2/a-kromka-v2.png, только desktop-контекст. Output: island-storyboard-initial.png.

```text
Use case: ui-mockup. Create ONE refined, precise static storyboard of a Windows desktop edge utility, inspired by the solid black unified Island concept at exuupery/island. This is a DESIGN STUDY, not a screenshot of implemented software, not a web dashboard, and not an animation. Approximately1536x1536px SQUARE canvas: exactly FOUR very wide horizontal desktop strips stacked vertically. Each strip shows the same upper320pixels of the same broad Windows desktop at consistent relative scale. Slim light label bands outside the desktop above each strip, clean sans-serif:
"01  ПОКОЙ"
"02  НАВЕДЕНИЕ · КРАЙ РЕАГИРУЕТ"
"03  ЗВУК"
"04  ОДИН КЛИК → СЕТЬ"
Quiet blue-gray sea-at-dusk Windows wallpaper, the upper corner of the SAME ordinary File Explorer window in lower far-right of each strip, with small "Проводник" title and credible minimize/maximize/close controls. No Apple menu bar. Match the previous concept's desktop context if supplied, but discard its old UI completely.

CORE OBJECT: A single solid near-black #070707 body attached FLUSH to the TOP CENTER EDGE of each desktop strip. Straight hidden top, softly rounded side shoulders, subtly organic lower outline. ONE continuous silhouette enclosing BOTH the navigation row and widget. No detached card or second floating toolbar, no gap, no connecting neck. Not transparent glass, no cyan, no reflective acrylic, no neon, no silver rim. Internal work groups may use very quiet #181818 fills; all controls white/gray. Segoe UI-like Russian typography. Mature, compact, calm desktop utility. Do not reproduce the reference's full dashboard, mascot, clock or other unrelated features.

PERSISTENT NAVIGATION IS ESSENTIAL. In states2,3,4 render the SAME TEN small white/gray line glyphs in EXACT same left-to-right order, EXACT same center positions and EXACT same Y offset from each desktop's top. The row is about360px wide with10equal36pxslots and belongs inside the continuous black body. Ordered symbols:
1 music note,
2 screenshot corner frame,
3 trash can,
4 charging plug,
5 kanban columns,
6 Wi-Fi,
7 speaker,
8 battery,
9 literal"RU",
10 notification bell.
No clock, no tray, no calendar, no ellipsis, NO BACK BUTTON, no search bar. Show all10 directly, never hidden in overflow. Active icon has a neutral medium-gray rounded rectangular selection pill occupying its EXISTING36pxslot; do not enlarge active slot or move any neighbors. Row is always centered at the same horizontal location even when body width changes. State2 neutral icons, state3 speaker(slot7) selected, state4 Wi-Fi(slot6) selected. The persistent row never disappears when a widget is open.

STATE1: only the lower2–3px of a96px-wide black resting seam, FLUSH AGAINST the top screenshot boundary, no wallpaper gap above it. All other island content hidden above edge, no status text or clock.
STATE2: the same body is now a compact400x54px black edge-tab containing the ten icons. Along its lower edge, slightly to the RIGHT of center near speaker slot, a visible white mouse cursor is below and outside the body. Show one shallow, smooth LOCAL DOWNWARD BULGE of the black lower contour, reaching toward the cursor by about10px over a90pxwide area. This is essential: a visible but restrained boundary deformation, not a drop, spike, teardrop, gooey drip or whole-body sag. Cursor tip within a fewpx of the bulge. The icon row remains perfectly straight and stable despite the boundary pulling. A thin small external annotation with a leader may say"Край тянется к курсору"; annotation stays OUTSIDE the island.
STATE3: clicked speaker expands the SAME continuous black body to approximately460x200px. Navigation stays at exactly the same positions at top with speaker selected. BELOW it, audio content inside same body: a compact label"Звук" atleft and"64%"atright; white horizontal volume slider; small inline output choices"Наушники"(selected with white check) and"Динамики"(unselected gray); below a microphone icon,"Микрофон", white slider and"54%". Two subtle internal dark group regions may organize output and mic, but must NOT resemble detached popup cards. Solid black exterior encloses everything, generous lower padding, rounded lower corners. Keep text14–16px, values18max. No back-chevron. Show a small cursor over the adjacent Wi-Fi navigation slot to signal the next direct click; speaker remains selected in this frame.
STATE4: after that direct click, SAME body becomes slightly narrower and TALLER, approximately420x270px. Same top-centered navigation slots and icons in exactly same positions, now Wi-Fi selected; speaker unselected. Inside: compact header"Wi-Fi" with a white ON toggle at right. Below exactly THREE rows of invented/demo networks: first"Домашняя" with small subtext"Подключено"and white check; second"Сеть 2"with neutral Wi-Fi icon; third"Сеть 3"with neutral Wi-Fi icon. No password, no IP address, no actual personal data, no popup or extra dialog. A small neutral footer"Демонстрационные сети"may be included inside content. Uniform neutral gray text and dividers. The increased height is obvious and the top-navigation positions are unchanged. No calendar.

MORPH COMMUNICATION: All four drawings are settled key forms except the cursor deformation in state2. To the LEFT outside the island in state4, a tiny thin neutral ghost-outline schematic may show the wider/shallow audio contour transitioning to the narrower/taller network contour, with one short arrow and label"Меняется само тело". This is only an editorial diagram OUTSIDE the UI, never a second ghost pane behind the live island. Do not clutter the screen with motion trails. Bottom caption outside the desktop:"Одна поверхность · лента остаётся · содержимое меняется".
Keep background context subordinate and identical, all four island centers aligned vertically. Accurate Russian text. Clean linework and restrained shadow. No giant dashboard, no decorative branding, no phone frames, no colored modules. The visible controls must be readable and navigation must clearly remain inside the same black object in both expanded states.
The attached previous A sheet is ONLY a context reference for the identical Windows wallpaper, broad desktop-strip framing and Explorer corner. Its transparent material, disappearing navigation and back control are REJECTED: replace them completely with the new single black body and persistent10-icon navigation described above. Recompose into FOUR equally wide strips, not three.
```

## 2. Первая коррекция

Inputs: island-storyboard-initial.png (edit target), ../../references/exuupery-controls-reference.png (style reference). Output: island-storyboard-correction-1.png. Коррекция не обеспечила нужную стабильность геометрии.

```text
Use case: precise-object-edit. INPUT1 is the FOUR-STATE WINDOWS STORYBOARD TO EDIT. INPUT2 is ONLY a STYLE REFERENCE from exuupery/island; do not edit or reproduce its full dashboard, purple background or extra functions. Keep INPUT1's four-row composition, seascape background, Explorer corners, labels, audio and Wi-Fi content, black materials, cursor, external annotation and small morph schematic. The continuous dark body with internal tabs and dark grouped controls is right.
CORRECT TWO MATERIAL UI ERRORS IN INPUT1:
(1) REST: In first row, the existing black visible tab is too thick. Replace it with a mere2–3pixel black seam, about96pxwide, flush with upper desktop boundary. The first desktop starts approximately y39 in the1254x1254input: black seam should occupy only y39–41; erase the rest of its old10pxheight using blue background. No gap above it.
(2) NAVIGATION GEOMETRY: In input1 the10icons change spacing between hover, sound, and Wi-Fi. They MUST remain at identical xpositions and identical yoffset from each desktop's top. Re-render the three navigation rows on ONE fixed400px-wide centered grid with10slots,40px per slot. On the1254pxwide sheet the ten ICON CENTERS should be approximately x447,487,527,567,607,647,687,727,767,807 in EACH of rows2,3,4. Use the SAME glyph sizes18px. Preserve order: music, screenshot, trash, charging plug, kanban, Wi-Fi, speaker, battery, RU, bell. Center y=top-of-desktop+26 in each row. Only selection changes: row2 all neutral; row3 SPEAKER slot7 selected with a compact gray rounded36pxhighlight; row4 Wi-Fi slot6 selected with same highlight. Selected highlight MUST NOT change its slot width or cause neighbors to shift. No back arrow or ellipsis. In row3 keep the visible cursor pointing at Wi-Fi slot6 while speaker remains selected.
Adjust ONLY ROW2's outer hover-body width to420px centered atx627 (roughly x417..837) so hover is narrower than sound. Keep row2's lower edge local smooth10pxbulge toward visible cursor near speaker slot7. Keep icon row perfectly straight. The body stays flush with top edge. Preserve row3's wider audio body (roughly496pxwide) and row4's narrower Wi-Fi body (roughly426pxwide) and differing heights, but keep navigation centers identical as above. If row4 needs2pxmore side padding, minimally extend silhouette without moving the icons.
Everything else stays the same. All tabs and widget content remain INSIDE ONE continuous black exterior, no detached floating strip. Avoid adding colored glow, glass or thick border. The subtle edge softness of INPUT2 may guide the drawing, but do not introduce a repeating wavy border or autonomous wiggle. Keep text readable and all demo content unchanged.
```

## 3. Финальная коррекция

Inputs: island-storyboard-initial.png (edit target), ../../references/exuupery-controls-reference.png (style reference). Output: island-storyboard-v3.png. Промпт просил равную ширину для наглядности этой пары; итог сохраняет небольшой raster-дрейф.

```text
Redraw a precise FOUR-STATE Windows UI storyboard. SQUARE1536x1536output. INPUT1 is a previous storyboard to improve; INPUT2 is only the approved BLACK MATERIAL / INNER TAB style reference. Preserve INPUT1's four wide desktop strips, blue seaside background, Explorer windows, Russian row labels, audio values and demo-network names. REDRAW ALL FOUR ISLAND UI SHAPES FROM SCRATCH to satisfy the following simpler design. The previous islands' dimensions and navigation placement are WRONG and must NOT be preserved.

THE KEY CORRECTION: THE THREE VISIBLE NAVIGATION ROWS MUST BE IDENTICAL COPIES IN GEOMETRY. All THREE revealed black bodies have the SAME WIDTH:530px in the input1254pxwide coordinate system, or about42%of the screenshot strip width. Same leftedge, same rightedge, same center. Their HEIGHTS differ. On hover the body is very shallow. With sound it is deeper. With Wi-Fi it is deepest. Do NOT narrow Wi-Fi. Do NOT widen hover. Do NOT resize, respace or replace any icon when changing widget. No decorative outlines or labels around individual icons.

Use one 10-slot row as a master and repeat it identically in states2,3,4. Same icon sizes, same padding, same centers. All10icons appear in each, in this exact order: music note, screenshot frame, trash, charging plug, kanban, Wi-Fi, speaker, battery, RU, bell. State2none selected; state3speaker selected; state4Wi-Fi selected. Highlight is simply a gray rounded rectangle BEHIND the existing icon, with no shift of any neighbor. All icons occupy fixed-width slots. No Back, no ellipsis, no clock, no calendar. Keep navigation at identical distance below each desktop's top.

STATE1 "01 ПОКОЙ": redraw the rest marker as a truly THIN straight black HAIRLINE, only2pxhigh and96pxwide, touching the top desktop edge. NOT a10pxdeeprounded tab. It is almost invisible. This is critical.
STATE2 "02 НАВЕДЕНИЕ · КРАЙ РЕАГИРУЕТ":530pxwide by50pxdeep solid black body, ten icons inside. Local lower-edge shallow bulge reaches about10px toward the visible white cursor below speaker slot. Rest of edge and icon row perfectly stable. Keep existing outside leader text"Край тянется к курсору". Body flush to topedge. No drips or spikes.
STATE3 "03 ЗВУК": SAME530pxwidth and SAME navigation positions, total height220px. Speaker selected. Below row inside SAME black body: "Звук", "64%", white horizontal volume slider, selected"Наушники" and unselected"Динамики", and microphone control"Микрофон","54%". Small quiet dark groups inside black silhouette. Cursor over adjacent Wi-Fi tab, ready for one-click change. No separate popup.
STATE4 "04 ОДИН КЛИК → СЕТЬ": SAME530pxwidth and SAME navigation positions, total height280px. Wi-Fi selected. Below: Wi-Fi header with ON toggle; "Домашняя" and"Подключено"; "Сеть 2"; "Сеть 3"; small"Демонстрационные сети". No passwordor personaldata. Plenty of black exterior surrounds interior content. Shape is taller than sound by60px, unmistakably same object.
Keep bottomcaption"Одна поверхность · лента остаётся · содержимое меняется". The small outside schematic should show SAME-WIDTH shallow and tall contours, not narrow/wide. Label"Меняется само тело". This is a static storyboard, no claim of animation.
Material: nearly pure opaque BLACK like reference2, neutral darkgray workgroups, whitecontrols, gray active-tabpill, subtle soft lowercorners, restrained shadow, no glass, no cyan, no colored light. Keep readable text and compact desktop scale. Do NOT add any extra functions fromreference2. The purpose is direct comparison of an IDENTICAL fixed navigation rail inside the SAME-WIDTH continuous body across three heights.
```

