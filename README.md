# Firebird — Просмотр данных

WPF desktop client (.NET 8) for browsing Firebird databases. The window is laid
out like a classic data-acquisition viewer: top menu, secondary tab bar, a
column-visibility panel on the left, the data grid filling the rest, and a
status bar at the bottom that updates as you move the cursor through cells.

The UI is in Russian. All bindable strings live in `Views/MainWindow.xaml`,
`Views/ConnectionDialog.xaml`, and `ViewModels/MainViewModel.cs` — easy to
re-localize.

## What's on screen

```
┌── Файл  Правка  Колонки  Настройка  Справка ────────────────────────────┐
├── Объект: [combo]                              Лимит строк: [1000] [Обновить] ┤
├──────────────────┬────── Графики │ Таблица │ Схема │ SQL ────────────────┤
│ Выбор столбцов   │                                                         │
│  ☑ Время         │   Time   Depth   Pressure   ...                         │
│  ☑ Гл.забоя      │    ...                                                  │
│  ☑ Давление      │                                                         │
│  ☐ Внешн.газ     │                                                         │
│  ...             │                                                         │
│ ┌──────────────┐ │                                                         │
│ │Вверх  Вниз ☐Все│                                                         │
│ └──────────────┘ │                                                         │
├──────────────────┴─────────────────────────────────────────────────────────┤
│ ┃Загружено┃ 1234 строк за 87 мс  Курсор: строка 5 · [Время] = 11:12:35.1   │
└────────────────────────────────────────────────────────────────────────────┘
```

- **Top menu**: Файл / Правка / Колонки / Настройка / Справка
- **Object picker**: a combo of all tables and views in the database; the
  window title becomes `Просмотр данных: «<table>»`
- **Tabs**: Графики, **Таблица** (default), Схема, SQL
- **Left panel "Выбор столбцов"**: one checkbox per column. Checking/unchecking
  hides the column in the grid in real time. The Up/Down buttons reorder the
  list and the grid columns to match. The three-state **Все** checkbox toggles
  everything on or off
- **Status bar**: green `Загружено` indicator when connected, row count + load
  time, current cursor position (row + column + value), and connection info on
  the right

## Solution layout

```
FirebirdViewer.sln
├── .gitignore
├── README.md
└── FirebirdViewer/
    ├── FirebirdViewer.csproj            (.NET 8, WPF, FirebirdSql.Data.FirebirdClient 10.3.1)
    ├── App.xaml / App.xaml.cs            entry point, global exception handler, resource merge
    ├── Views/
    │   ├── MainWindow.xaml(.cs)          main window (Russian UI, bindings)
    │   └── ConnectionDialog.xaml(.cs)    modal connection dialog
    ├── ViewModels/
    │   ├── ObservableObject.cs           INPC base
    │   └── MainViewModel.cs              all state and commands
    ├── Models/
    │   ├── ConnectionSettings.cs
    │   ├── DatabaseObject.cs
    │   ├── ColumnVisibility.cs           one row in the column toggle list
    │   └── AppSettings.cs                persisted JSON root
    ├── Services/
    │   ├── IFirebirdService.cs           data-layer abstraction
    │   ├── FirebirdService.cs            FbConnection / FbDataAdapter implementation
    │   ├── ISettingsService.cs           settings persistence abstraction
    │   └── SettingsService.cs            JSON in %APPDATA%\FirebirdViewer
    ├── Commands/
    │   ├── RelayCommand.cs
    │   └── AsyncRelayCommand.cs
    ├── Converters/
    │   └── BoolToVisibilityConverter.cs  + InverseBooleanConverter
    └── Resources/
        └── Styles.xaml                   shared brushes, control styles
```

## Prerequisites

- Windows + .NET 8 SDK
- Visual Studio 2022 17.8+ with the *.NET desktop development* workload (or
  any editor that handles `dotnet build`)
- A reachable Firebird server (2.5 / 3.0 / 4.0 / 5.0)

## Build & run

In Visual Studio: open `FirebirdViewer.sln`, press **F5**.

From the command line:

```powershell
dotnet restore
dotnet build
dotnet run --project FirebirdViewer
```

## Connecting

`Файл → Подключиться...` opens a modal dialog. Fill in:

| Поле       | Что вводить                                                              |
| ---------- | ------------------------------------------------------------------------ |
| Сервер     | Адрес Firebird-сервера (`localhost`, `192.168.1.10`, и т.д.)             |
| Порт       | TCP-порт (по умолчанию `3050`)                                           |
| База       | Путь или алиас, видимый сервером (например `C:\data\DATABASE.FDB`)      |
| Пользователь | `CREATOR`                                                              |
| Пароль     | `ehkvfDF5` (предзаполнен для демо)                                       |
| Кодировка  | `UTF8` / `WIN1251` / `ISO8859_1` / `NONE`                                |

Recent connections are saved (without passwords) under
`%APPDATA%\FirebirdViewer\settings.json` and appear under
`Файл → Недавние подключения` — clicking one re-opens the dialog pre-filled.

## Features

- Modal connection dialog with field validation
- Object combo lists tables + views; selecting one loads data, schema, and
  indexes for it
- **Column visibility checkboxes** that hide / show grid columns instantly
- **Up / Down** to reorder columns; **Все** (tri-state) to toggle all on/off
- **Status bar cursor** showing the currently focused cell (row, column name,
  value)
- **Графики** tab — placeholder, ready to wire a chart library
- **Схема** tab — sub-tabs for Колонки and Индексы
- **SQL** tab — multi-line editor; F5 runs it
- CSV export for the current grid and for SQL results (UTF-8 BOM, proper
  escaping)
- Persisted: last connection (no password), recent connections, row limit
- Keyboard shortcuts: F5 in the SQL tab, Ctrl+F5 to refresh selected object

## Implementation notes

- DataGrid column visibility uses `AutoGeneratingColumn` to look up each
  column in the `Columns` collection and apply its `IsVisible` flag, then
  subscribes to PropertyChanged so toggling the checkbox updates the grid
  immediately.
- Up/Down moves an item in `ObservableCollection<ColumnVisibility>` and
  raises a `ColumnOrderChanged` event; the View re-syncs each
  DataGridColumn's `DisplayIndex` to match.
- Cursor info is captured in `DataGrid.SelectedCellsChanged`: row index +
  column header + cell value, formatted in Russian for the status bar.
- The "Все" checkbox is genuinely tri-state — it shows an indeterminate
  visual when some columns are visible and others aren't.

## Troubleshooting

| Сообщение об ошибке                                | Что обычно значит                                 |
| -------------------------------------------------- | ------------------------------------------------- |
| *Unable to complete network request to host*       | Сервер недоступен, неверный хост или брандмауэр   |
| *Your user name and password are not defined*      | Неверные учётные данные                           |
| *I/O error during 'CreateFile' (open) operation*   | Неверный путь к базе или нет прав чтения у Firebird |
| *Cannot attach to password database*               | Проблема на стороне сервера: `security?.fdb`      |
| *Token unknown - line X, column Y*                 | Синтаксическая ошибка в SQL-запросе               |

## License

Internal / unspecified. Adjust to fit your needs.
