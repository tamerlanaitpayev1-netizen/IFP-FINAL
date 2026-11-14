namespace ArenaQuiz.Client.Services;

/// <summary>
/// Lightweight reactive localization service supporting Kazakh (kk), Russian (ru), and English (en).
/// </summary>
public sealed class LocalizationService
{
    public event Action? OnLanguageChanged;

    public string CurrentLanguage { get; private set; } = "en";

    private static readonly Dictionary<string, (string En, string Ru, string Kk)> Dictionary = new(StringComparer.Ordinal)
    {
        ["nav.lobby"] = ("Lobby", "Главная", "Басты бет"),
        ["nav.play"] = ("Play", "Играть", "Ойнау"),
        ["nav.host"] = ("Host Studio", "Кабинет ведущего", "Жүргізуші студиясы"),
        ["nav.analytics"] = ("Results", "Результаты", "Нәтижелер"),
        ["nav.auth"] = ("Account", "Аккаунт", "Аккаунт"),

        ["home.badge.rt"] = ("Live Multiplayer", "Реальное время", "Нақты уақыт"),
        ["home.badge.stack"] = ("Cross-Platform PWA", "Кроссплатформа", "Кроссплатформа"),
        ["home.title"] = ("Real-Time Quiz Arena Built for Speed and Integrity.", "Интерактивные викторины в реальном времени с защитой от читерства.", "Жылдамдық пен әділдікке негізделген нақты уақыттағы викторина."),
        ["home.subtitle"] = ("Host live sessions with server-verified timers, Classic Speed, Team Battle, and Knockout modes across desktop, tablet, and phone.", "Запускайте живые комнаты с серверным таймером в режимах классики, командной битвы и выбывания на любом устройстве.", "Серверлік таймермен классика, командалық шайқас және нокаут режимдерінде кез келген құрылғыдан ойнаңыз."),
        ["home.open_host"] = ("Open Host Studio", "Открыть студию ведущего", "Жүргізуші студиясын ашу"),
        ["home.view_results"] = ("View Results & CSV", "История и экспорт CSV", "Нәтижелер және CSV"),
        ["home.join_title"] = ("Join a Live Room", "Войти в комнату", "Бөлмеге қосылу"),
        ["home.pin"] = ("Room PIN", "Код комнаты", "Бөлме коды"),
        ["home.nick"] = ("Nickname", "Никнейм", "Лақап ат"),
        ["home.team"] = ("Team (Team Battle)", "Команда (для командного режима)", "Команда"),
        ["home.join_btn"] = ("Join Arena", "Войти в игру", "Ойынға кіру"),
        ["home.open_rooms"] = ("Active Open Lobbies", "Активные комнаты", "Ашық бөлмелер"),
        ["home.refresh"] = ("Refresh", "Обновить", "Жаңарту"),
        ["home.library"] = ("Quiz Library", "Библиотека викторин", "Викториналар кітапханасы"),

        ["host.title"] = ("Host Studio", "Студия ведущего", "Жүргізуші студиясы"),
        ["host.tab.live"] = ("Live Game Console", "Управление игрой", "Ойынды басқару"),
        ["host.tab.editor"] = ("Quiz Editor", "Редактор квизов", "Квиз редакторы"),
        ["host.signin_notice"] = ("Sign in with a Host account to create rooms and edit quizzes.", "Войдите под учётной записью ведущего, чтобы запускать комнаты и редактировать квизы.", "Бөлме ашу және квиз өңдеу үшін жүргізуші аккаунтына кіріңіз."),
        ["host.signin_btn"] = ("Sign In as Host", "Войти как ведущий", "Жүргізуші ретінде кіру"),
        ["host.projector"] = ("Projector View", "Экран проектора", "Проектор режимі"),
        ["host.pause"] = ("Pause Timer", "Пауза", "Кідірту"),
        ["host.resume"] = ("Resume Timer", "Продолжить", "Жалғастыру"),

        ["play.title"] = ("Enter Play Arena", "Вход в игру", "Ойынға кіру"),
        ["play.wait"] = ("Waiting for Host to Start", "Ожидаем старта от ведущего", "Жүргізушінің бастауын күтуде"),
        ["play.locked"] = ("Answer Locked In", "Ответ зафиксирован сервером", "Жауап серверде тіркелді"),

        ["analytics.title"] = ("Results & Question Analytics", "Аналитика игр и сложных вопросов", "Ойын нәтижелері және талдау"),
        ["analytics.export"] = ("Export CSV", "Скачать CSV", "CSV жүктеу"),
        ["analytics.hardest"] = ("Hardest Questions", "Самые сложные вопросы", "Ең қиын сұрақтар"),

        ["auth.title"] = ("Account & Access", "Аккаунт и права доступа", "Аккаунт және рұқсаттар"),
        ["auth.login_btn"] = ("Sign In", "Войти", "Кіру"),
        ["auth.player_switch"] = ("Continue as Guest Player", "Войти как гость-игрок", "Қонақ ойыншы ретінде кіру")
    };

    public void SetLanguage(string lang)
    {
        string normalized = lang.ToLowerInvariant() switch
        {
            "ru" => "ru",
            "kk" => "kk",
            _ => "en"
        };

        if (CurrentLanguage != normalized)
        {
            CurrentLanguage = normalized;
            OnLanguageChanged?.Invoke();
        }
    }

    public string T(string key)
    {
        if (!Dictionary.TryGetValue(key, out var entry))
        {
            return key;
        }

        return CurrentLanguage switch
        {
            "ru" => entry.Ru,
            "kk" => entry.Kk,
            _ => entry.En
        };
    }
}

