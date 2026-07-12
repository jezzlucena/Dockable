/* ============================================================================
   Dockable — landing page localization (en / pt-BR / es / uk / zh-Hans)
   ----------------------------------------------------------------------------
   Mirrors the app's approach (Localization/LocData.cs): in-code string tables
   keyed by a stable key; English is the source-of-truth and the fallback. On
   first visit the language follows the browser; the user's choice is persisted.

   Markup hooks (set by applyAll):
     data-i18n="key"        → element.textContent
     data-i18n-html="key"   → element.innerHTML   (values may contain markup)
     data-i18n-label="key"  → element's data-label attribute (dock tooltips)
     data-i18n-aria="key"   → element's aria-label attribute

   Exposes window.Dockable = { lang, t(key), setLang(code), langs, onLangChange(cb) }.
   script.js reads this for the clock locale, theme-toggle labels, and to re-split
   the scroll-reveal letters when the language changes.
   ============================================================================ */
(function () {
  // Picker order + native names (matches the app's language list).
  const LANGS = [
    ["en", "English"],
    ["pt-BR", "Português (Brasil)"],
    ["es", "Español"],
    ["uk", "Українська"],
    ["zh-Hans", "中文"],
  ];

  const GH = 'https://github.com/jezzlucena';

  const STRINGS = {
    en: {
      meta_title: "Dockable — A macOS-style Dock for Windows 11",
      meta_desc: "Dockable brings the macOS Dock to Windows 11: magnifying icons, a Liquid Glass bar, a menu bar, and Genie, Suck & Scale minimize effects. Light, Dark & Auto.",
      nav_launcher: "Launcher", nav_effects: "Effects", nav_glass: "Liquid Glass", nav_themes: "Themes", nav_menubar: "Menu Bar", nav_steam: "Get on Steam",
      theme_word: "Theme", theme_auto: "Auto", theme_light: "Light", theme_dark: "Dark",
      aria_switch_theme: "Switch theme", aria_language: "Language", aria_home: "Dockable home",
      hero_tagline: "The macOS Dock, reimagined for Windows 11.",
      hero_sub: "Magnifying icons. A Liquid Glass bar. A menu bar. And windows that warp into the dock with Genie, Suck & Scale. Light, Dark, or Auto — it follows your system.",
      cta_steam: "Get it on Steam", cta_github: "View on GitHub", scroll: "Scroll",
      label_start: "Start", label_browser: "Browser", label_mail: "Mail", label_music: "Music", label_photos: "Photos", label_code: "Code", label_files: "Files", label_prefs: "Preferences", label_trash: "Recycle Bin",
      statement_main: "A dock that feels like it was always part of Windows.",
      statement_dim: "Pinned and running apps, magnified on hover, in a bar of real glass.",
      word_launcher: "App Launcher",
      launcher_lead: "Your taskbar, magnified.",
      launcher_text: "Dockable mirrors your pinned and running apps in a single glass bar. Glide across it and icons swell with a smooth fisheye — the same raised-cosine falloff as the Mac. Drag to reorder, drop a file to pin, fling one off to remove it.",
      launcher_p1: "Live magnification with adjustable size & radius",
      launcher_p2: "Dock-owned pins, seeded once from your taskbar",
      launcher_p3: "Running indicators & open-app bounce",
      launcher_hint: "↑ Try the dock above — move your cursor across it.",
      effects_main: "Windows don't just vanish.", effects_dim: "They warp into the dock — pick your style.",
      word_genie: "Genie", genie_lead: "The classic stretch & pour.",
      genie_text: "A true 3-D mesh warp bends the window through a glassy neck and pours it into its dock tile — captured frame-perfect so there's no black flash.",
      word_suck: "Suck", suck_lead: "A hard funnel, straight down.",
      suck_text: "The window collapses into a tight point above the dock and drops in — fast, punchy, and oddly satisfying.",
      word_scale: "Scale", scale_lead: "Simple. Quick. Clean.",
      scale_text: "Prefer restraint? The window simply scales down and glides to its tile. Tune every effect's tempo with the Effect Speed slider.",
      word_glass: "Liquid Glass", glass_lead: "Real refraction. Real blur. A living rim.",
      glass_text: "Three bar styles — Translucent, Acrylic, and Liquid Glass: a runtime pixel shader that blurs and saturates the desktop behind the bar, bends light at a curved rim, and drifts a slow specular sheen across the surface.",
      word_themes: "Light. Dark.<br>Auto.",
      themes_lead: "It matches your system — or you choose.",
      themes_text: "Every surface is themed. Pick Light, Dark, or Auto, which follows the Windows app theme and re-paints the instant you switch.",
      tile_light: "Light", tile_dark: "Dark", tile_auto: "Auto",
      word_menubar: "Menu Bar", menubar_lead: "A familiar strip across the top.",
      menubar_text: "Flip on the optional menu bar and a thin, always-acrylic strip docks to the top of your primary screen — a macOS-style system menu, the focused app's real name, a keyboard-layout switcher, Quick Settings, Notifications, and a clock.",
      mb_file: "File", mb_edit: "Edit", mb_view: "View",
      grid_title: "Every detail, considered.",
      card1_h: "Five languages", card1_p: "English, Português (BR), Español, Українська & 中文 — switched live.",
      card2_h: "Drag to reorder", card2_p: "A free-roaming ghost follows your cursor; hold steady to remove a pin.",
      card3_h: "Recycle Bin", card3_p: "Drop files to delete; a state-aware empty/full icon, far right.",
      card4_h: "Per-monitor DPI", card4_p: "Crisp on any display, with an AppBar that reserves just the bar.",
      card5_h: "Taskbar, your way", card5_p: "Keep it Always, Auto-hide it, or hide it entirely. Restored on exit.",
      card6_h: "Built native", card6_p: "C# · WPF · .NET 9 · CsWin32. Light, fast, and unpackaged.",
      download_h: "Bring your desktop to life.", download_p: "Dockable for Windows 11 — available on Steam.", download_note: "Steam link coming soon.",
      footer_credit: `Created by <a href="${GH}" target="_blank" rel="noopener">Jezz Lucena</a> · Inspired by Apple's macOS Dock.`,
      footer_fine: "Dockable is an independent project and is not affiliated with Apple or Microsoft. macOS and Windows are trademarks of their respective owners.",
      footer_vibe: "Proudly vibe coded.",
    },

    "pt-BR": {
      meta_title: "Dockable — um Dock estilo macOS para o Windows 11",
      meta_desc: "O Dockable traz o Dock do macOS para o Windows 11: ícones com ampliação, uma barra de Vidro Líquido, uma barra de menus e os efeitos de minimizar Aladim, Sucção e Escala. Clara, Escura e Automática.",
      nav_launcher: "Lançador", nav_effects: "Efeitos", nav_glass: "Vidro Líquido", nav_themes: "Temas", nav_menubar: "Barra de menus", nav_steam: "Baixar na Steam",
      theme_word: "Tema", theme_auto: "Automática", theme_light: "Clara", theme_dark: "Escura",
      aria_switch_theme: "Alternar tema", aria_language: "Idioma", aria_home: "Início do Dockable",
      hero_tagline: "O Dock do macOS, reinventado para o Windows 11.",
      hero_sub: "Ícones que ampliam. Uma barra de Vidro Líquido. Uma barra de menus. E janelas que se transformam para dentro do dock com Aladim, Sucção e Escala. Clara, Escura ou Automática — acompanha o seu sistema.",
      cta_steam: "Baixar na Steam", cta_github: "Ver no GitHub", scroll: "Role",
      label_start: "Iniciar", label_browser: "Navegador", label_mail: "E-mail", label_music: "Música", label_photos: "Fotos", label_code: "Código", label_files: "Arquivos", label_prefs: "Preferências", label_trash: "Lixeira",
      statement_main: "Um dock que parece sempre ter feito parte do Windows.",
      statement_dim: "Apps fixados e em execução, ampliados ao passar o cursor, numa barra de vidro de verdade.",
      word_launcher: "Lançador de apps",
      launcher_lead: "Sua barra de tarefas, ampliada.",
      launcher_text: "O Dockable espelha seus apps fixados e em execução em uma única barra de vidro. Passe o cursor e os ícones crescem com um efeito olho de peixe suave — a mesma curva de cosseno elevado do Mac. Arraste para reordenar, solte um arquivo para fixar, jogue um para fora para remover.",
      launcher_p1: "Ampliação ao vivo com tamanho e raio ajustáveis",
      launcher_p2: "Fixados gerenciados pelo dock, semeados uma vez a partir da barra de tarefas",
      launcher_p3: "Indicadores de apps abertos e salto ao abrir",
      launcher_hint: "↑ Experimente o dock acima — mova o cursor sobre ele.",
      effects_main: "As janelas não somem sem mais nem menos.", effects_dim: "Elas se transformam para dentro do dock — escolha seu estilo.",
      word_genie: "Aladim", genie_lead: "O clássico esticar e despejar.",
      genie_text: "Uma verdadeira deformação de malha 3D dobra a janela por um gargalo de vidro e a despeja na sua miniatura do dock — capturada quadro a quadro, sem nenhum flash preto.",
      word_suck: "Sucção", suck_lead: "Um funil direto, para baixo.",
      suck_text: "A janela colapsa em um ponto estreito acima do dock e mergulha — rápido, marcante e estranhamente satisfatório.",
      word_scale: "Escala", scale_lead: "Simples. Rápido. Limpo.",
      scale_text: "Prefere algo discreto? A janela apenas diminui e desliza até sua miniatura. Ajuste o ritmo de cada efeito com o controle de Velocidade do efeito.",
      word_glass: "Vidro líquido", glass_lead: "Refração real. Desfoque real. Uma borda viva.",
      glass_text: "Três estilos de barra — Translúcido, Acrílico e Vidro líquido: um shader de pixel em tempo de execução que desfoca e satura a área de trabalho atrás da barra, curva a luz em uma borda arredondada e desliza um brilho especular lento pela superfície.",
      word_themes: "Clara. Escura.<br>Automática.",
      themes_lead: "Combina com o seu sistema — ou você escolhe.",
      themes_text: "Cada superfície tem tema. Escolha Clara, Escura ou Automática, que acompanha o tema do Windows e se repinta na hora em que você troca.",
      tile_light: "Clara", tile_dark: "Escura", tile_auto: "Automática",
      word_menubar: "Barra de menus", menubar_lead: "Uma faixa familiar no topo.",
      menubar_text: "Ative a barra de menus opcional e uma faixa fina, sempre acrílica, encaixa no topo da tela principal — um menu de sistema ao estilo macOS, o nome real do app em foco, um seletor de layout de teclado, Configurações Rápidas, Notificações e um relógio.",
      mb_file: "Arquivo", mb_edit: "Editar", mb_view: "Exibir",
      grid_title: "Cada detalhe, pensado.",
      card1_h: "Cinco idiomas", card1_p: "English, Português (BR), Español, Українська e 中文 — trocados ao vivo.",
      card2_h: "Arraste para reordenar", card2_p: "Um fantasma livre segue seu cursor; segure parado para remover um item fixado.",
      card3_h: "Lixeira", card3_p: "Solte arquivos para excluir; um ícone que muda entre cheia e vazia, na ponta direita.",
      card4_h: "DPI por monitor", card4_p: "Nítido em qualquer tela, com um AppBar que reserva apenas a barra.",
      card5_h: "Barra de tarefas do seu jeito", card5_p: "Deixe-a Sempre visível, em ocultar automático ou totalmente oculta. Restaurada ao sair.",
      card6_h: "Nativo de verdade", card6_p: "C# · WPF · .NET 9 · CsWin32. Leve, rápido e sem instalação.",
      download_h: "Dê vida à sua área de trabalho.", download_p: "Dockable para Windows 11 — disponível na Steam.", download_note: "Link da Steam em breve.",
      footer_credit: `Criado por <a href="${GH}" target="_blank" rel="noopener">Jezz Lucena</a> · Inspirado no Dock do macOS da Apple.`,
      footer_fine: "O Dockable é um projeto independente e não tem afiliação com a Apple ou a Microsoft. macOS e Windows são marcas registradas de seus respectivos proprietários.",
      footer_vibe: "Programado no maior vibe, com orgulho.",
    },

    es: {
      meta_title: "Dockable — un Dock al estilo de macOS para Windows 11",
      meta_desc: "Dockable lleva el Dock de macOS a Windows 11: iconos que se amplían, una barra de Cristal Líquido, una barra de menús y los efectos de minimizar Genio, Succión y Escala. Claro, Oscuro y Automático.",
      nav_launcher: "Lanzador", nav_effects: "Efectos", nav_glass: "Cristal Líquido", nav_themes: "Temas", nav_menubar: "Barra de menús", nav_steam: "Conseguir en Steam",
      theme_word: "Tema", theme_auto: "Automático", theme_light: "Claro", theme_dark: "Oscuro",
      aria_switch_theme: "Cambiar tema", aria_language: "Idioma", aria_home: "Inicio de Dockable",
      hero_tagline: "El Dock de macOS, reinventado para Windows 11.",
      hero_sub: "Iconos que se amplían. Una barra de Cristal Líquido. Una barra de menús. Y ventanas que se transforman dentro del dock con Genio, Succión y Escala. Claro, Oscuro o Automático: sigue a tu sistema.",
      cta_steam: "Conseguir en Steam", cta_github: "Ver en GitHub", scroll: "Desliza",
      label_start: "Inicio", label_browser: "Navegador", label_mail: "Correo", label_music: "Música", label_photos: "Fotos", label_code: "Código", label_files: "Archivos", label_prefs: "Preferencias", label_trash: "Papelera de reciclaje",
      statement_main: "Un dock que parece haber sido siempre parte de Windows.",
      statement_dim: "Apps ancladas y en ejecución, ampliadas al pasar el cursor, en una barra de cristal de verdad.",
      word_launcher: "Lanzador de apps",
      launcher_lead: "Tu barra de tareas, ampliada.",
      launcher_text: "Dockable refleja tus apps ancladas y en ejecución en una sola barra de cristal. Pasa el cursor y los iconos crecen con un suave ojo de pez: la misma curva de coseno elevado que en el Mac. Arrastra para reordenar, suelta un archivo para anclar, lanza uno fuera para quitarlo.",
      launcher_p1: "Ampliación en vivo con tamaño y radio ajustables",
      launcher_p2: "Anclados gestionados por el dock, sembrados una vez desde tu barra de tareas",
      launcher_p3: "Indicadores de apps abiertas y rebote al abrir",
      launcher_hint: "↑ Prueba el dock de arriba: mueve el cursor sobre él.",
      effects_main: "Las ventanas no desaparecen sin más.", effects_dim: "Se transforman dentro del dock: elige tu estilo.",
      word_genie: "Genio", genie_lead: "El clásico estirar y verter.",
      genie_text: "Una auténtica deformación de malla 3D dobla la ventana por un cuello de cristal y la vierte en su miniatura del dock, capturada fotograma a fotograma para que no haya destello negro.",
      word_suck: "Succión", suck_lead: "Un embudo directo, hacia abajo.",
      suck_text: "La ventana se contrae en un punto estrecho sobre el dock y se sumerge: rápido, contundente y curiosamente satisfactorio.",
      word_scale: "Escala", scale_lead: "Simple. Rápido. Limpio.",
      scale_text: "¿Prefieres algo sobrio? La ventana simplemente se reduce y se desliza hasta su miniatura. Ajusta el ritmo de cada efecto con el control de Velocidad del efecto.",
      word_glass: "Cristal líquido", glass_lead: "Refracción real. Desenfoque real. Un borde vivo.",
      glass_text: "Tres estilos de barra — Translúcido, Acrílico y Cristal líquido: un shader de píxeles en tiempo de ejecución que desenfoca y satura el escritorio tras la barra, curva la luz en un borde redondeado y desliza un brillo especular lento por la superficie.",
      word_themes: "Claro. Oscuro.<br>Automático.",
      themes_lead: "Combina con tu sistema, o tú eliges.",
      themes_text: "Cada superficie tiene tema. Elige Claro, Oscuro o Automático, que sigue el tema de Windows y se repinta en el instante en que cambias.",
      tile_light: "Claro", tile_dark: "Oscuro", tile_auto: "Automático",
      word_menubar: "Barra de menús", menubar_lead: "Una franja familiar en la parte superior.",
      menubar_text: "Activa la barra de menús opcional y una franja fina, siempre acrílica, se acopla en la parte superior de tu pantalla principal: un menú de sistema al estilo macOS, el nombre real de la app en foco, un selector de distribución de teclado, Configuración rápida, Notificaciones y un reloj.",
      mb_file: "Archivo", mb_edit: "Editar", mb_view: "Ver",
      grid_title: "Cada detalle, cuidado.",
      card1_h: "Cinco idiomas", card1_p: "English, Português (BR), Español, Українська y 中文 — cambiados en vivo.",
      card2_h: "Arrastra para reordenar", card2_p: "Un fantasma libre sigue tu cursor; mantén quieto para quitar un anclado.",
      card3_h: "Papelera de reciclaje", card3_p: "Suelta archivos para eliminarlos; un icono que cambia entre llena y vacía, a la derecha del todo.",
      card4_h: "DPI por monitor", card4_p: "Nítido en cualquier pantalla, con un AppBar que reserva solo la barra.",
      card5_h: "La barra de tareas a tu manera", card5_p: "Déjala Siempre visible, con ocultar automático u oculta del todo. Se restaura al salir.",
      card6_h: "Nativo de verdad", card6_p: "C# · WPF · .NET 9 · CsWin32. Ligero, rápido y sin instalación.",
      download_h: "Dale vida a tu escritorio.", download_p: "Dockable para Windows 11 — disponible en Steam.", download_note: "Enlace de Steam próximamente.",
      footer_credit: `Creado por <a href="${GH}" target="_blank" rel="noopener">Jezz Lucena</a> · Inspirado en el Dock de macOS de Apple.`,
      footer_fine: "Dockable es un proyecto independiente y no está afiliado a Apple ni a Microsoft. macOS y Windows son marcas comerciales de sus respectivos propietarios.",
      footer_vibe: "Hecho con buena vibra, y con orgullo.",
    },

    uk: {
      meta_title: "Dockable — Dock у стилі macOS для Windows 11",
      meta_desc: "Dockable переносить Dock із macOS на Windows 11: збільшення значків, панель «Рідке скло», рядок меню та ефекти згортання «Джин», «Всмоктування» і «Масштаб». Світла, Темна й Авто.",
      nav_launcher: "Запуск", nav_effects: "Ефекти", nav_glass: "Рідке скло", nav_themes: "Теми", nav_menubar: "Рядок меню", nav_steam: "Завантажити в Steam",
      theme_word: "Тема", theme_auto: "Авто", theme_light: "Світла", theme_dark: "Темна",
      aria_switch_theme: "Змінити тему", aria_language: "Мова", aria_home: "Головна Dockable",
      hero_tagline: "Dock із macOS, переосмислений для Windows 11.",
      hero_sub: "Значки, що збільшуються. Панель «Рідке скло». Рядок меню. І вікна, що згортаються в док із ефектами «Джин», «Всмоктування» та «Масштаб». Світла, Темна чи Авто — слідує за вашою системою.",
      cta_steam: "Завантажити в Steam", cta_github: "Переглянути на GitHub", scroll: "Гортайте",
      label_start: "Пуск", label_browser: "Браузер", label_mail: "Пошта", label_music: "Музика", label_photos: "Фото", label_code: "Код", label_files: "Файли", label_prefs: "Параметри", label_trash: "Кошик",
      statement_main: "Док, який ніби завжди був частиною Windows.",
      statement_dim: "Закріплені та запущені програми, збільшені під курсором, у панелі зі справжнього скла.",
      word_launcher: "Запуск програм",
      launcher_lead: "Ваша панель завдань, зі збільшенням.",
      launcher_text: "Dockable відображає ваші закріплені та запущені програми в одній скляній панелі. Проведіть курсором — і значки плавно збільшуються ефектом «риб'яче око», тією ж кривою піднятого косинуса, що й на Mac. Перетягуйте, щоб упорядкувати, киньте файл, щоб закріпити, відкиньте значок, щоб прибрати.",
      launcher_p1: "Живе збільшення з регульованим розміром і радіусом",
      launcher_p2: "Закріплення, керовані доком, заповнені один раз із панелі завдань",
      launcher_p3: "Індикатори відкритих програм і підстрибування під час запуску",
      launcher_hint: "↑ Спробуйте док угорі — проведіть по ньому курсором.",
      effects_main: "Вікна не просто зникають.", effects_dim: "Вони згортаються в док — оберіть свій стиль.",
      word_genie: "Джин", genie_lead: "Класичне розтягування та переливання.",
      genie_text: "Справжня деформація 3D-сітки згинає вікно крізь скляну шийку й переливає його в плитку дока — захоплене покадрово, тож жодного чорного спалаху.",
      word_suck: "Всмоктування", suck_lead: "Жорстка лійка, прямо вниз.",
      suck_text: "Вікно стискається у вузьку точку над доком і занурюється — швидко, чітко й на диво приємно.",
      word_scale: "Масштаб", scale_lead: "Просто. Швидко. Чисто.",
      scale_text: "Любите стриманість? Вікно просто зменшується й ковзає до своєї плитки. Налаштуйте темп кожного ефекту повзунком «Швидкість ефекту».",
      word_glass: "Рідке скло", glass_lead: "Справжня рефракція. Справжнє розмиття. Живий край.",
      glass_text: "Три стилі панелі — «Напівпрозорий», «Акрил» і «Рідке скло»: піксельний шейдер у реальному часі розмиває й насичує робочий стіл за панеллю, заломлює світло на округлому краю та проводить повільний блиск по поверхні.",
      word_themes: "Світла. Темна.<br>Авто.",
      themes_lead: "Відповідає вашій системі — або обираєте ви.",
      themes_text: "Кожна поверхня має тему. Оберіть «Світла», «Темна» чи «Авто», що слідує за темою Windows і перефарбовується миттєво, щойно ви перемикаєте.",
      tile_light: "Світла", tile_dark: "Темна", tile_auto: "Авто",
      word_menubar: "Рядок меню", menubar_lead: "Знайома смуга вгорі.",
      menubar_text: "Увімкніть необов'язковий рядок меню — і тонка, завжди акрилова смуга закріплюється вгорі основного екрана: системне меню в стилі macOS, справжня назва активної програми, перемикач розкладки клавіатури, Швидкі параметри, Сповіщення та годинник.",
      mb_file: "Файл", mb_edit: "Редагувати", mb_view: "Вигляд",
      grid_title: "Кожну деталь продумано.",
      card1_h: "П'ять мов", card1_p: "English, Português (BR), Español, Українська та 中文 — перемикаються наживо.",
      card2_h: "Перетягуйте, щоб упорядкувати", card2_p: "Вільний привид слідує за курсором; потримайте нерухомо, щоб прибрати закріплення.",
      card3_h: "Кошик", card3_p: "Киньте файли, щоб видалити; значок змінює стан між повним і порожнім, скраю праворуч.",
      card4_h: "DPI для кожного монітора", card4_p: "Чітко на будь-якому екрані, з AppBar, що резервує лише панель.",
      card5_h: "Панель завдань на ваш смак", card5_p: "Залиште «Завжди», приховуйте автоматично або сховайте повністю. Відновлюється під час виходу.",
      card6_h: "Справді нативно", card6_p: "C# · WPF · .NET 9 · CsWin32. Легко, швидко й без встановлення.",
      download_h: "Оживіть свій робочий стіл.", download_p: "Dockable для Windows 11 — доступно в Steam.", download_note: "Посилання на Steam незабаром.",
      footer_credit: `Створив <a href="${GH}" target="_blank" rel="noopener">Jezz Lucena</a> · Натхненно Dock із macOS від Apple.`,
      footer_fine: "Dockable — незалежний проєкт, не пов'язаний з Apple чи Microsoft. macOS і Windows є торговими марками відповідних власників.",
      footer_vibe: "З гордістю зроблено на вайбі.",
    },

    "zh-Hans": {
      meta_title: "Dockable — 适用于 Windows 11 的 macOS 风格程序坞",
      meta_desc: "Dockable 将 macOS 的程序坞带到 Windows 11：放大的图标、液态玻璃栏、菜单栏，以及神奇、吸入和缩放最小化效果。浅色、深色与自动。",
      nav_launcher: "启动台", nav_effects: "效果", nav_glass: "液态玻璃", nav_themes: "主题", nav_menubar: "菜单栏", nav_steam: "在 Steam 获取",
      theme_word: "主题", theme_auto: "自动", theme_light: "浅色", theme_dark: "深色",
      aria_switch_theme: "切换主题", aria_language: "语言", aria_home: "Dockable 主页",
      hero_tagline: "macOS 程序坞，为 Windows 11 重新设计。",
      hero_sub: "会放大的图标。液态玻璃栏。菜单栏。以及通过神奇、吸入和缩放效果卷入程序坞的窗口。浅色、深色或自动——跟随你的系统。",
      cta_steam: "在 Steam 获取", cta_github: "在 GitHub 查看", scroll: "滚动",
      label_start: "开始", label_browser: "浏览器", label_mail: "邮件", label_music: "音乐", label_photos: "照片", label_code: "代码", label_files: "文件", label_prefs: "偏好设置", label_trash: "回收站",
      statement_main: "一个仿佛本就属于 Windows 的程序坞。",
      statement_dim: "固定和运行中的应用，悬停即放大，置于真正的玻璃栏中。",
      word_launcher: "应用启动台",
      launcher_lead: "你的任务栏，放大呈现。",
      launcher_text: "Dockable 将你固定和运行中的应用映射到一条玻璃栏中。划过它，图标会以平滑的鱼眼效果放大——与 Mac 相同的升余弦衰减。拖动可重新排序，拖入文件即可固定，甩出即可移除。",
      launcher_p1: "实时放大，大小与半径可调",
      launcher_p2: "由程序坞管理的固定项，首次从任务栏导入",
      launcher_p3: "已打开应用的指示点与打开时的弹跳",
      launcher_hint: "↑ 试试上面的程序坞——把光标移过去。",
      effects_main: "窗口不会凭空消失。", effects_dim: "它们会卷入程序坞——选择你的风格。",
      word_genie: "神奇", genie_lead: "经典的拉伸与倾泻。",
      genie_text: "真正的 3D 网格变形让窗口穿过玻璃般的颈部，倾泻进它在程序坞中的缩略图——逐帧捕捉，没有黑屏闪烁。",
      word_suck: "吸入", suck_lead: "笔直向下的漏斗。",
      suck_text: "窗口在程序坞上方收拢成一个细点并坠入——快速、利落，出奇地令人满足。",
      word_scale: "缩放", scale_lead: "简单。快速。干净。",
      scale_text: "偏爱克制？窗口只是缩小并滑向它的缩略图。用「效果速度」滑块调节每种效果的节奏。",
      word_glass: "液态玻璃", glass_lead: "真实折射。真实模糊。灵动的边缘。",
      glass_text: "三种栏样式——半透明、亚克力和液态玻璃：运行时像素着色器模糊并增饱和栏后的桌面，在圆润的边缘弯折光线，并让缓慢的高光在表面流动。",
      word_themes: "浅色。深色。<br>自动。",
      themes_lead: "跟随你的系统——或由你选择。",
      themes_text: "每个界面都有主题。选择浅色、深色或自动；自动会跟随 Windows 应用主题，并在你切换的瞬间重新上色。",
      tile_light: "浅色", tile_dark: "深色", tile_auto: "自动",
      word_menubar: "菜单栏", menubar_lead: "顶部一条熟悉的横栏。",
      menubar_text: "开启可选的菜单栏，一条纤薄、始终亚克力的横栏便会停靠在主屏幕顶部——macOS 风格的系统菜单、当前应用的真实名称、键盘布局切换器、快速设置、通知，以及时钟。",
      mb_file: "文件", mb_edit: "编辑", mb_view: "查看",
      grid_title: "每个细节，都经过考量。",
      card1_h: "五种语言", card1_p: "English、Português (BR)、Español、Українська 与 中文——实时切换。",
      card2_h: "拖动重新排序", card2_p: "自由漂浮的幻影跟随光标；按住不动即可移除固定项。",
      card3_h: "回收站", card3_p: "拖入文件即可删除；最右侧有随状态变化的空/满图标。",
      card4_h: "逐显示器 DPI", card4_p: "在任何显示器上都清晰，AppBar 仅预留栏本身的空间。",
      card5_h: "任务栏，随你掌控", card5_p: "可设为始终显示、自动隐藏或完全隐藏。退出时自动恢复。",
      card6_h: "原生打造", card6_p: "C# · WPF · .NET 9 · CsWin32。轻量、快速、免安装。",
      download_h: "让你的桌面活起来。", download_p: "Dockable for Windows 11——现已上架 Steam。", download_note: "Steam 链接即将公布。",
      footer_credit: `由 <a href="${GH}" target="_blank" rel="noopener">Jezz Lucena</a> 创作 · 灵感来自 Apple 的 macOS 程序坞。`,
      footer_fine: "Dockable 是独立项目，与 Apple 或 Microsoft 无关。macOS 和 Windows 是其各自所有者的商标。",
      footer_vibe: "凭感觉编程，自豪出品。",
    },
  };

  const STORE_KEY = "dockable-lang";

  function resolveInitial() {
    const saved = localStorage.getItem(STORE_KEY);
    if (saved && STRINGS[saved]) return saved;
    const navs = navigator.languages || [navigator.language || "en"];
    for (const raw of navs) {
      const l = (raw || "").toLowerCase();
      if (l.startsWith("pt")) return "pt-BR";
      if (l.startsWith("es")) return "es";
      if (l.startsWith("uk")) return "uk";
      if (l.startsWith("zh")) return "zh-Hans";
      if (l.startsWith("en")) return "en";
    }
    return "en";
  }

  let current = resolveInitial();
  const listeners = [];

  function t(key) {
    const tbl = STRINGS[current];
    if (tbl && tbl[key] != null) return tbl[key];
    return STRINGS.en[key] != null ? STRINGS.en[key] : key;
  }

  function applyAll() {
    document.documentElement.lang = current;
    document.querySelectorAll("[data-i18n]").forEach((el) => { el.textContent = t(el.getAttribute("data-i18n")); });
    document.querySelectorAll("[data-i18n-html]").forEach((el) => { el.innerHTML = t(el.getAttribute("data-i18n-html")); });
    document.querySelectorAll("[data-i18n-label]").forEach((el) => { el.setAttribute("data-label", t(el.getAttribute("data-i18n-label"))); });
    document.querySelectorAll("[data-i18n-aria]").forEach((el) => { el.setAttribute("aria-label", t(el.getAttribute("data-i18n-aria"))); });
    document.title = t("meta_title");
    const md = document.querySelector('meta[name="description"]');
    if (md) md.setAttribute("content", t("meta_desc"));
    const sel = document.getElementById("langSelect");
    if (sel) sel.value = current;
    // Let dependent modules (clock locale, theme labels, scroll-letter split) re-render.
    listeners.forEach((cb) => { try { cb(current); } catch (e) { /* keep going */ } });
  }

  function setLang(code) {
    if (!STRINGS[code] || code === current) return;
    current = code;
    localStorage.setItem(STORE_KEY, code);
    applyAll();
  }

  window.Dockable = {
    get lang() { return current; },
    t,
    setLang,
    langs: LANGS,
    onLangChange(cb) { if (typeof cb === "function") listeners.push(cb); },
  };

  // Build the language picker and wire it.
  const sel = document.getElementById("langSelect");
  if (sel) {
    LANGS.forEach(([code, name]) => {
      const opt = document.createElement("option");
      opt.value = code; opt.textContent = name;
      sel.appendChild(opt);
    });
    sel.value = current;
    sel.addEventListener("change", () => setLang(sel.value));
  }

  applyAll();
})();
