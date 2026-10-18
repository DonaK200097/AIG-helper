# AI Mode — этап 1: разведка и запись в план питания

## Сверка имён из плана с репозиторием

| В плане | В репозитории | Примечание |
| --- | --- | --- |
| `PowerNative` | `app/Mode/PowerNative.cs` | P/Invoke PowrProf объявлены `private`. Чтобы не трогать апстрим, AI Mode объявляет свои в `PowrProfSettings.cs` (плюс корректный `LocalFree` после `PowerGetActiveScheme`). Публичные `PowerGetEffectiveOverlayScheme` и `powerModes` переиспользуются. |
| `HardwareControl` | `app/HardwareControl.cs` (статический класс) | `cpuTemp`, `gpuTemp`, `cpuPower`/`GetCPUPower()`, `gpuPower`, `cpuFanRPM`, `gpuFanRPM`, `gpuUse`, `cpuUsage`. Понадобится на этапе 2. |
| `AppConfig` | `app/AppConfig.cs` | `GetMode`/`SetMode` дают ключ `name_<mode>` — ровно формат `ai_mode_<mode>` из плана. |
| `Fans` | `app/Fans.cs`, `Fans.Designer.cs` | Секция CPU — `panelPower` → `panelCPU`, `panelCPUTemp`, `panelApplyPower`. |
| `ModeControl` | `app/Mode/ModeControl.cs`, `SetPerformanceMode` | Точка подключения — после установки режима питания Windows и `auto_boost`. |
| Сон | `Program.SystemEvents_PowerModeChanged` (Suspend), `Settings.WndProc` (`PBT_APMSUSPEND`/`PBT_APMRESUMEAUTOMATIC`) | После пробуждения `Settings.cs` вызывает `SetAutoModes(wakeup: true)` → `SetPerformanceMode`. |
| AC / батарея | `Program.OnPowerSettled` → `SetAutoModes(powerChanged: true)` | |
| Выход | `Program.OnExit` (`Application.ApplicationExit`), `SessionEnding` | |
| Детект Armoury Crate | `AsusService.IsArmouryRunning()` | Службы `ArmouryCrate.Service`, `LightingService`. |
| Режим питания Windows | `PowerNative.SetPowerMode`, ключи `powermode_<mode>`, `skip_powermode` | |
| Intel / AMD | `PawnIO.CpuInfo.IsAMD` | На AMD AI Mode ничего не делает. |
| Логи | `Logger.appPath` = `%AppData%\GHelper` | `ai_restore.json` лежит там же. |

## Что сделано

- `IPowerSettings` / `PowrProfSettings` — чтение и запись значений схемы через PowrProf, за интерфейсом.
- `AiPowerPlan` — снимок исходных AC/DC значений четырёх параметров в память и `ai_restore.json`, запись (только AC, одинаково для P- и E-ядер, без записи при совпадении, частота с шагом 100 МГц, 1800…0, EPP от значения режима до 80), восстановление. Не зависит от WinForms и G-Helper, поэтому тестируется на любой ОС.
- `AiModeHooks` — точки подключения: восстановление при старте, после смены режима, при выходе, завершении сессии, сне и необработанном исключении.
- Тесты: `tests/AiMode.Tests` (`dotnet test tests/AiMode.Tests`).

Правила безопасности:

- Снимок пишется на диск до первой записи в план. Не удалось записать файл — в план ничего не пишется.
- Если `ai_restore.json` уже существует, именно он считается исходными значениями (а не текущие, возможно уже урезанные).
- Запись идёт только в схему из снимка и только пока она активна. Сменилась схема — сначала восстановление в старую.
- Неудачное восстановление оставляет файл, следующий запуск повторит.

## Отладочный флаг

В `%AppData%\GHelper\config.json`:

```json
"ai_debug_cap": 3000,
"ai_debug_epp": 60
```

Любой из ключей можно не указывать (параметр не трогается). Значения применяются при каждой установке режима (старт, смена режима, пробуждение). Удалить ключи и сменить режим — значения вернутся.

В `log.txt` появятся строки:

```
AI Plan: snapshot <scheme> FreqMax=0/0, FreqMax1=0/0, EPP=25/50, EPP1=25/50
AI Debug: cap=3000 epp=60 OK | <scheme> FreqMax=3000, FreqMax1=3000, EPP=60, EPP1=60
AI Overlay: effective ded574b5-... (Best Performance) | 961cc777-...: EPP=- EPP1=- | ded574b5-...: EPP=- EPP1=-
AI Plan: restore (exit) OK: ...
```

## Проверка на ноутбуке (приёмка этапа 1)

1. Прописать `ai_debug_cap: 3000`, запустить G-Helper.
2. `powercfg /qh scheme_current 54533251-82be-4824-96c1-47b60b740d00 75b0ae3f-bce0-45a7-8c89-c9611c25e100` — текущее AC-значение `0x00000bb8` (3000). То же для `...e101`. В HWiNFO под нагрузкой частота ядер не выше 3 ГГц.
3. Выйти из G-Helper через «Выход» — значение снова исходное (обычно `0x00000000`), `ai_restore.json` удалён.
4. Запустить снова, убить процесс через диспетчер задач — `ai_restore.json` остался, значение 3000. Запустить G-Helper — в логе `found restore file from previous run` и `restore (recovery) OK`, значение исходное (и сразу снова 3000, пока флаг стоит).

## Открытый вопрос: перекрывает ли оверлей EPP

Код это закрыть не может — нужен прогон на GU605CR:

1. `ai_debug_epp: 60`.
2. По очереди выбрать в G-Helper режимы, у которых режим питания Windows — Best Power Efficiency, Balanced, Best Performance (или выставить `powermode_<mode>`).
3. Для каждого сравнить фактический EPP в HWiNFO (ядро → «EPP») или ThrottleStop с 60.
4. Строка `AI Overlay` в логе показывает, какой оверлей был эффективным, и читаются ли у оверлейных схем собственные значения EPP (`-` — не читаются через PowrProf).

Если при каком-то оверлее EPP ≠ 60 — оверлей перекрывает, и на этапе 3 при включённом AI Mode ставить оверлей Balanced и блокировать его смену. Результат записать сюда.

## Не входит в этап 1

Батарея (AI Mode там выключен, отработка — этап 3 вместе с контроллером), предупреждение про Armoury Crate, детект конфликтов с другими программами — этап 3.
