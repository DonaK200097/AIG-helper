using System.Globalization;

namespace GHelper.AiMode
{
    /// <summary>
    /// UI strings for AI Mode. Russian when the UI culture is Russian, English otherwise.
    /// </summary>
    internal static class AiText
    {
        static bool Ru => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

        static string T(string en, string ru) => Ru ? ru : en;

        public static string Title => "AI Mode";
        public static string Enable => T("AI Mode (dynamic CPU frequency cap)", "AI Mode (динамический потолок частоты)");
        public static string Target => T("Target temperature", "Целевая температура");
        public static string Offset => T("Offset", "Офсет");
        public static string AggressiveAbove(int t) => T($"aggressive above {t}°C", $"агрессивно выше {t}°C");
        public static string MinFreq => T("Minimum frequency", "Минимальная частота");
        public static string TargetFreq => T("Target frequency (heavy load)", "Целевая частота (под нагрузкой)");
        public static string Load => T("load", "загрузка");
        public static string Ceiling => T("Load limit", "Лимит по нагрузке");
        public static string Log => T("Record telemetry log", "Запись лога телеметрии");
        public static string OpenLogs => T("Logs", "Логи");

        public static string Source => T("Source", "Источник");
        public static string Regulator => T("Regulator", "Регулятор");
        public static string Explore => T("Exploration", "Исследование");
        public static string Cap => T("Cap", "Потолок");
        public static string NoLimit => T("no limit", "без лимита");
        public static string MHz => T("MHz", "МГц");

        public static string Off => T("Off", "Выключен");
        public static string LogOnly => T("Log only", "Только лог");
        public static string Paused => T("Paused", "Пауза");
        public static string Stopped => T("Stopped", "Остановлен");

        public static string Zone(AiZone? zone, bool idle)
        {
            if (idle) return T("idle", "простой");
            return zone switch
            {
                AiZone.Cold => T("cold", "холодная"),
                AiZone.Working => T("working", "рабочая"),
                AiZone.Aggressive => T("aggressive", "агрессивная"),
                AiZone.Emergency => T("emergency", "аварийная"),
                _ => "",
            };
        }

        public static string Reason(string reason) => reason switch
        {
            "battery" => T("on battery", "от батареи"),
            "sensor" => T("no CPU temperature", "нет температуры CPU"),
            "snapshot" => T("can't read power plan", "не читается план питания"),
            "conflict" => T("plan keeps being changed by another program", "план постоянно меняет другая программа"),
            "starting" => T("starting…", "запуск…"),
            "write errors" => T("power plan write errors", "ошибки записи плана"),
            "Armoury Crate" => "Armoury Crate",
            _ => reason,
        };

        public static string WarningTitle => "AI Mode";
        public static string ArmouryWarning => T(
            "Armoury Crate is running. AI Mode won't start while it controls the power plan.",
            "Запущен Armoury Crate. Пока он управляет планом питания, AI Mode не запустится.");
        public static string OverlayWarning => T(
            "This mode switches Windows power mode (not Balanced). It may override EPP set by AI Mode; frequency cap still works.",
            "Этот режим переключает режим питания Windows (не «Сбалансированный»). Он может перекрывать EPP от AI Mode; потолок частоты работает.");
        public static string BatteryWarning => T(
            "On battery AI Mode is paused, it starts when the charger is connected.",
            "На батарее AI Mode на паузе, запустится при подключении зарядки.");
    }
}
