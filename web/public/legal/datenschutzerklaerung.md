# Datenschutzerklärung für Ember

> **English summary (not binding; the German text below is the binding version).**
> Ember is a free habit tracker. You can use it through a Discord bot, a web app and an Android app. The Android app is a direct download (no Google Play) and checks the same server for new versions. The operator runs Ember on a server in Germany. You can use Ember with an account (synced across your devices) or without one. Without an account your data stays on your device, but the app still asks the server for service notices. An account needs an email address and a password, or a Discord login. Habit names, check-ins, low days, day notes, habit notes, moods, let-go slips and "What helps me" can reveal health information, so Ember stores them only with your explicit consent (GDPR Art. 9(2)(a)). Routine names, habit renames and push subscriptions do not need that consent yet. You can withdraw the consent as easily as you gave it, in Settings under "Privacy" or with `/privacy withdraw`: Ember then saves nothing new, reminders stop, your data is export-only, and Ember deletes it after 30 days, with a reminder 3 days before. The same applies if you choose "No thanks". The private journal can be encrypted end to end, so the server then holds only ciphertext for day notes, habit notes and moods. Fireside is off by default. Turning it on asks for its own explicit consent, because the people you link with see your chosen name, when you ask for warmth and, if you allow it per person, whether you checked in today. Ember shows no ads, uses no third-party analytics or tracking services and no AI, and sells no data. The operator counts usage himself, only from sign-in and app-use times, never from health data, and you can object. He reads the feedback you send in a private dashboard; the daily feedback email only says how many reports came in. Emails that Ember sends through Resend can be read by the operator in Resend's dashboard. The "I need a moment" flow stores and sends nothing; only a reflection you choose to save goes into your private journal. Discord carries your messages as the platform. You can export your data and delete everything at once.

---

**Stand:** [[Stand: Release-Datum]]

Ember befindet sich in einer Beta-Phase. Funktionen können sich noch ändern. Diese Erklärung beschreibt den Stand vom oben genannten Datum.

Ember ist ein kostenloser Habit Tracker. Diese Erklärung sagt dir, welche Daten Ember verarbeitet, warum, wie lange und welche Rechte du hast.

## 1. Wer ist verantwortlich?

Verantwortlich im Sinne der Datenschutz-Grundverordnung (DSGVO) ist:

Nico Pauly Wöllnitz
Erzbergerstraße 129
46145 Oberhausen
Deutschland

E-Mail: contact@nicowoellnitz.de

Einen Datenschutzbeauftragten gibt es derzeit nicht. Bei allen Fragen zum Datenschutz schreib an die E-Mail-Adresse oben.

## 2. Das Wichtigste in Kürze

- Ember speichert nur, was die Funktionen brauchen: dein Konto, deine Habits, Check-ins, Tagesnotizen und Erinnerungen.
- Habit-Namen, Check-ins, Tiefs, Tagesnotizen, Habit-Notizen, Stimmungen, Ausrutscher mit Gründen und „Was mir hilft“ können etwas über deine Gesundheit verraten. Ember speichert sie nur mit deiner ausdrücklichen Einwilligung. Du kannst sie jederzeit so einfach widerrufen, wie du sie gegeben hast (Abschnitt 4). Routinen-Namen brauchen bisher keine Einwilligung.
- Das private Tagebuch kannst du Ende-zu-Ende verschlüsseln. Dann liegen Tagesnotizen, Habit-Notizen, eigene Stimmungen und „Was mir hilft“ auf dem Server nur verschlüsselt.
- Am Lagerfeuer (Fireside) ist standardmäßig aus. Beim Einschalten fragt Ember eigens nach deiner ausdrücklichen Einwilligung. Die Menschen, mit denen du dich verbindest, sehen dann deinen Namen, ob du heute um Wärme bittest und, wenn du es pro Person erlaubst, ob du heute eingecheckt hast (Abschnitt 3.13).
- Der Ablauf „Ich brauche einen Moment“ speichert und sendet nichts. Nur eine Reflexion, die du bewusst speicherst, landet im privaten Tagebuch (Abschnitt 3.12).
- Der Betreiber liest Feedback, auch Bilder, in seinem Dashboard. Die tägliche Feedback-E-Mail nennt nur die Zahl neuer Meldungen. Die E-Mails, die Ember verschickt, kann der Betreiber in seinem Resend-Konto einsehen (Abschnitt 6.6).
- Keine Werbung, keine Tracking-Dienste, keine Analyse-Dienste von Drittanbietern, keine KI. Der Betreiber wertet Nutzungszahlen selbst aus (Abschnitt 6.6): nur Summen, keine einzelnen Personen, und nur aus Anmelde- und Nutzungszeiten, nie aus Gesundheitsdaten. Wir verkaufen keine Daten.
- Du kannst deine Daten jederzeit exportieren und alles auf einmal löschen. Backups behalten Daten noch einige Wochen (Abschnitt 7).

## 3. Welche Daten verarbeitet Ember?

Ember legt Daten nur an, wenn du eine Funktion nutzt, die sie braucht. Du kannst Ember auch ganz ohne Konto nutzen (siehe 3.9).

### 3.1 Konto und Anmeldung

| Daten | Wofür |
|---|---|
| E-Mail-Adresse und Passwort (das Passwort nur als nicht umkehrbaren Hash) | Anmeldung, Bestätigung der E-Mail-Adresse und Zurücksetzen des Passworts. Konten, die nur mit Discord angelegt wurden, haben keine E-Mail-Adresse. |
| Anmeldung mit E-Mail-Adresse: gehashter Bestätigungscode, sein Ablauf, die Zahl der Versuche und ein Hash des Geräteschlüssels, den die App mitschickt | Damit nur das Gerät, das die Anmeldung begonnen hat, sie mit dem Code abschließen kann. Der Eintrag gilt, bis der Code benutzt oder durch einen neuen ersetzt wird. Nicht bestätigte Konten löscht Ember nach 7 Tagen (Abschnitt 7). |
| Anmeldeversuche: Zahl der fehlgeschlagenen Versuche und Ende einer Sperre | Schutz vor Ratenangriffen auf dein Passwort. Die Zahl setzt sich bei der nächsten erfolgreichen Anmeldung zurück. |
| Discord-Nutzer-ID, wenn du Discord verbunden hast | Damit du dich mit Discord anmelden kannst und der Discord-Bot dich deinem Konto zuordnet. Ember fragt bei Discord die Berechtigung „identify“ an. Discord liefert dabei auch Name und Profilbild. Ember übernimmt davon nur die Nutzer-ID und verwirft den Rest. |
| Zwei-Faktor-Anmeldung: geheimer Schlüssel für deine Authenticator-App und Wiederherstellungscodes. Ein Wiederherstellungscode gilt nur einmal und wird nach der Benutzung entwertet. | Damit der Anmeldecode geprüft und die Anmeldung zusätzlich geschützt werden kann. |
| Anmeldesitzungen: eine Sitzungs-ID pro Anmeldung auf einem Gerät, Zeitpunkt der Anmeldung, Ablaufzeit und letzte Nutzung. Keine IP-Adresse. | Damit du dich auf einem Gerät oder auf allen Geräten abmelden kannst (Abschnitt 8). |
| Zeitzone, Design, Gnadentage, Einstellung für die zweite Erinnerung, Anzeige der Streaks ein oder aus, Sprache, Ruhiger Modus und die Tipps, die du schon gesehen hast | Damit „heute“, Streaks und Erinnerungen nach deiner Uhrzeit laufen. Hast du keine Zeitzone gewählt, übernimmt die App die Zeitzone deines Geräts. Die Anzeige der Streaks schaltest du unter „Habits & Tage“ in den Einstellungen. Für neue Konten ist sie aus. Die Sprache gilt für die App, E-Mails, Erinnerungen und den Discord-Bot. Neue Konten übernehmen sie aus der Anmeldung oder der Discord-Sprache. Die gesehenen Tipps speichert Ember, damit einmalige Hinweise nicht wiederkommen. |
| Einwilligungsnachweis: Zeitpunkt, Version der Datenschutzerklärung und der Tag der letzten Abfrage. Hast du „Nein danke“ gewählt, steht dort das Ende der 30-Tage-Pause. Hast du widerrufen oder „Nein danke“ gewählt, außerdem der Zeitpunkt davon und wann Ember dich an die Löschung erinnert hat. Für Am Lagerfeuer der Zeitpunkt und die Version deiner Einwilligung. | Damit wir nachweisen können, dass du eingewilligt hast (Art. 7 Abs. 1 DSGVO), dich bei einer neuen Version erneut fragen können und deine Daten nach einem Widerruf rechtzeitig löschen (Abschnitt 4). |
| Erstellungszeitpunkt des Kontos | Steckt in der Konto-ID (UUIDv7). Ember speichert ihn nicht als eigenes Feld. Damit werden nie bestätigte Konten nach 7 Tagen gelöscht (Abschnitt 7). |

### 3.2 Habits, Check-ins und Routinen

Gesundheitsbezogene Daten, siehe Abschnitt 4.

| Daten | Wofür |
|---|---|
| Habit-Name (höchstens 100 Zeichen), Art (aufbauen oder loslassen), Häufigkeit (täglich, an bestimmten Wochentagen oder x-mal pro Woche), Anzahl Check-ins pro Tag, Erinnerungszeit, Pause (erster und letzter pausierter Tag), die Erinnerungsziele, die du für diesen Habit ausgeschaltet hast, Erstellungszeitpunkt | Damit Ember deine Habits anzeigt, auswertet und dich daran erinnert. |
| Routine: Name (höchstens 50 Zeichen), Erinnerungszeit, die Erinnerungsziele, die du für diese Routine ausgeschaltet hast, welche Habits dazugehören und Erstellungszeitpunkt | Damit du Habits zusammenfassen und gemeinsam erinnert werden kannst. |
| Check-ins: Datum (ohne Uhrzeit), erledigt oder nicht, Anzahl; bei Check-ins über Discord außerdem die ID des Servers, auf dem du sie gemacht hast (leer bei Check-ins in einer Direktnachricht) | Damit Ember Streaks sowie Wochen- und Jahresübersichten berechnen kann. Du kannst vergangene Tage ab dem Start eines Habits nachtragen, ändern oder leeren. |
| Ausrutscher bei Habits zum Loslassen: Datum, Anzahl pro Tag und die Gründe, die du angibst (gestresst, gelangweilt, müde, einsam, mit anderen zusammen, etwas anderes) | Damit Ember die freien Tage zählt und du Ausrutscher notieren kannst. Ein Ausrutscher gehört zum Habit und wird mit ihm gelöscht. Gründe wie „einsam“ oder „gestresst“ können etwas über deine Gesundheit verraten, deshalb gilt für sie Abschnitt 4. |
| Tiefs: das Datum jedes Tages, den du als Tief markierst. Ein Tief gilt für alle deine Habits. | Ein Tief schützt deine Streaks an diesem Tag. Du kannst es jederzeit wieder entfernen. |
| Gnadentage pro Woche (0 bis 3) | Ein verpasster Tag beendet deinen Streak nicht sofort. |
| Nächster geplanter Erinnerungszeitpunkt (UTC) und, falls eine zweite Erinnerung ansteht, ihr Zeitpunkt | Damit Erinnerungen pünktlich kommen, auch bei Sommer- und Winterzeit. |
| Erinnerungen in Discord, die noch Schaltflächen haben: Kanal-ID, Nachrichten-ID und Tag | Damit Ember die Nachricht schließen kann, sobald sie nicht mehr gebraucht wird. Der Eintrag wird gelöscht, wenn die Nachricht geschlossen ist. |
| Zustellstatus der Erinnerungs-DMs: Zahl der fehlgeschlagenen Zustellungen in Folge, Zeitpunkt der Pause und des Hinweises | Wenn Discord deine Nachrichten mehrmals hintereinander nicht zustellen kann (zum Beispiel weil du DMs von Servermitgliedern ausgeschaltet hast), pausiert Ember die Erinnerungen und sagt dir das einmal. |

Ausrutscher und Gründe liegen im Klartext auf dem Server, auch wenn du das private Tagebuch eingeschaltet hast.

### 3.3 Tagebuch und Stimmungen

Gesundheitsbezogene Daten, siehe Abschnitt 4.

| Daten | Wofür |
|---|---|
| Tagesnotiz: Text (höchstens 2000 Zeichen) und/oder Stimmung für einen Tag | Damit du deinen Tag festhalten kannst. Eine Tagesnotiz gehört zum ganzen Tag, nicht zu einem einzelnen Habit. |
| Habit-Notiz (optional): kurzer Text (höchstens 280 Zeichen) zu einem Habit an einem Tag | Damit du zu einem Habit etwas festhalten kannst. Ember fragt nie von sich aus danach. Nur wenn du „Nach dem Check-in eine Notiz anbieten“ einschaltest, bietet Ember dir nach einem Check-in ein Feld dafür an. Diese Einstellung speichert Ember bei deinem Konto. |
| Eigene Stimmungen: Bezeichnung (höchstens 24 Zeichen), optional ein Emoji, ob sie archiviert ist, und Erstellungszeitpunkt | Damit du eigene Stimmungen auswählen kannst. Archivierte Stimmungen bleiben an den Tagen sichtbar, an denen du sie gesetzt hast. |

Tagesnotizen, Habit-Notizen und Stimmungen liegen **im Klartext** auf dem Server, solange du das private Tagebuch (3.4) nicht einschaltest.

Eine Tagesnotiz kann auch Reflexionen aus dem Ablauf „Ich brauche einen Moment“ enthalten (Abschnitt 3.12).

### 3.4 Privates Tagebuch (optional, Ende-zu-Ende verschlüsselt)

Schaltest du das private Tagebuch ein, erzeugt dein Browser oder deine App einen zufälligen Schlüssel. Er wird mit einer Passphrase und einem Wiederherstellungscode verschlüsselt, die du selbst festlegst beziehungsweise erhältst. Tagesnotizen, Habit-Notizen und eigene Stimmungen werden auf deinem Gerät verschlüsselt, bevor sie zum Server gelangen.

- Auf dem Server liegen nur verschlüsselte Einträge, der verschlüsselte Schlüssel sowie Salt und Parameter für die Entschlüsselung.
- Passphrase und Wiederherstellungscode verlassen dein Gerät nicht.
- Verlierst du beides, kann niemand die Einträge wieder lesen, auch wir nicht.
- Auch mit dem privaten Tagebuch bleiben E-Mail-Adresse, Discord-Nutzer-ID, Habit- und Routinen-Namen, Check-ins mit Datum, Tiefs und Ausrutscher mit Gründen für den Server lesbar, weil Erinnerungen und Statistiken sie brauchen. Außerdem sieht der Server, an welchen Tagen du eine Tagesnotiz hast, zu welchem Habit und an welchem Tag du eine Habit-Notiz hast, wie lang der verschlüsselte Text ist und welche Stimmung du gewählt hast (als Kennung).
- „Was mir hilft“ gibt es nur mit eingeschaltetem Tagebuch. Die Liste (höchstens 16 KiB) verschlüsselt dein Gerät, bevor sie zum Server geht. Der Server kann sie nicht lesen. Schaltest du das Tagebuch aus, löscht Ember die Liste.
- Schaltest du es wieder aus, lädt dein Gerät die Einträge wieder im Klartext hoch.

### 3.5 Erinnerungen und Benachrichtigungen

Erinnerungen gehen an jedes Ziel, das du in den Einstellungen eingeschaltet hast: Discord, jeden Browser mit Web-Push und jedes Telefon mit der App. Für jeden Habit und jede Routine kannst du einzelne Ziele ausschalten; Ember speichert dann nur, welche das sind.

- **Discord-Direktnachrichten:** Erinnerungen, die Einladung, einen offenen Tag nachzutragen, Hinweise zu Pausen und Lücken, die Wochenübersicht und die Erinnerung vor einer Löschung nach einem Widerruf (Abschnitt 4). Die Nachrichten laufen über Discord (siehe 6.1). Ember fragt einmal per Direktnachricht, ob du Erinnerungen auch in Discord möchtest, und speichert deine Antwort und wann es gefragt hat.
- **Web-Push (Benachrichtigungen im Browser):** Wenn du sie einschaltest, speichert Ember die Adresse (Endpunkt), die dein Browser beim Push-Dienst bekommt, zwei Schlüssel für die Verschlüsselung und den Zeitpunkt, zu dem du das Abo zuletzt gespeichert hast. Dazu kommen eine Bezeichnung des Browsers, die Ember aus dessen User-Agent bildet (zum Beispiel „Firefox on Linux“), ob Erinnerungen dort ein- oder ausgeschaltet sind, und die Anmeldesitzung, in der du das Abo gespeichert hast. Damit eine Erinnerung verschwindet, sobald du den Habit erledigt hast, merkt sich Ember, an welchen Habit oder welche Routine es an diesem Tag per Web-Push erinnert hat. Der Knopf „Erledigt“ auf einer Web-Push-Erinnerung funktioniert mit einem einmaligen Code. Ember speichert davon nur einen Hash, gebunden an Habit und Tag. Die Nachricht ist verschlüsselt. Standardmäßig nennt sie keinen Habit-Namen, weil sie auf dem Sperrbildschirm erscheinen kann. In den Einstellungen kannst du für jeden Browser einschalten, dass sie den Namen des Habits oder der Routine nennt; Ember speichert diese Wahl beim Abo.
- **Android-App:** Erinnerungen plant die App auf deinem Gerät, und zwar bis zu 14 Tage im Voraus, damit sie auch ohne Internet kommen. Das Widget auf dem Startbildschirm zeigt deine heutigen Habits und kann einen Habit auch abhaken, ohne dass du die App öffnest (Abschnitt 3.8). Beim Abmelden leert die App beides. Mit Konto meldet sich die App beim Server als Erinnerungsziel an: mit einer zufälligen Kennung dieser Installation, einer Gerätebezeichnung aus dem Modellnamen im User-Agent der App (zum Beispiel „Pixel 8“, sonst „Android phone“), ob Erinnerungen dort ein- oder ausgeschaltet sind, der Anmeldesitzung und dem Zeitpunkt der letzten Anmeldung. So kannst du das Telefon in den Einstellungen sehen und ausschalten. Der Eintrag gehört zu dieser Anmeldesitzung und wird mit ihr gelöscht.

### 3.6 Daten, die Ember nur kurz verwendet

- **Dein Discord-Benutzername**, wenn du mit `/habit share` eine Streak-Karte in einem Kanal postest. Der Name steht dann auf der Karte. Ember speichert ihn nicht.
- **Technische Angaben zu jeder Discord-Interaktion**, zum Beispiel aus welchem Server oder Kanal ein Befehl kommt. Ember nutzt sie nur, um zu antworten. Gespeichert wird davon nur die Server-ID bei einem Check-in.
- **Deine IP-Adresse:** Ember nutzt sie im Arbeitsspeicher, um zu viele Anfragen abzuwehren (Ratenbegrenzung). Bei IPv6 nutzt Ember nur das Netz (die ersten 64 Bit). Für E-Mail-Adressen merkt sich Ember nur einen Hash, ebenfalls nur im Arbeitsspeicher. Die Zähler landen nicht in der Datenbank und sind nach einem Neustart weg. Ausnahme sind die Fehlversuche bei Fireside-Codes, die dein Konto speichert (Abschnitt 3.13).

### 3.7 Technische Protokolle (Logs)

Die Dienste schreiben technische Protokolle, zum Beispiel wenn eine Erinnerung nicht zugestellt werden kann oder ein Fehler auftritt. Darin stehen Nutzer-IDs (intern und Discord-Nutzer-IDs), IDs von Push-Abos, interne Nummern von Habits, Zeitpunkte, Fehlermeldungen und Stack-Traces (die Abfolge der Programmteile bei einem Fehler). Habit-Namen stehen dort nicht. E-Mail-Adressen stehen dort im Betrieb nicht; bei E-Mails stehen nur der Betreff und die Domain des Empfängers drin. Die Protokolle dienen nur dazu, Fehler zu finden und den Betrieb sicher zu halten. Für Bot und API werden sie nach Größe rotiert: pro Dienst bleiben höchstens drei Dateien mit je 10 MB, ältere Einträge werden dabei überschrieben.

### 3.8 Cookies und Speicher im Browser und in der App

Ember setzt keine Cookies für Werbung oder Tracking. Diese Cookies sind technisch notwendig, damit Anmeldung und Schutz funktionieren:

| Name | Zweck | Dauer |
|---|---|---|
| `__Host-Ember` | Hält dich angemeldet. Nur über HTTPS, für JavaScript nicht lesbar. | Bis du den Browser schließt. Mit „Angemeldet bleiben“ und bei jeder Anmeldung mit Discord 14 Tage, bei jeder Nutzung verlängert. |
| `XSRF-TOKEN` | Schutz vor Anfragen, die andere Seiten in deinem Namen senden. | Sitzung |
| ASP.NET-Schutz-Cookie für Formulare | Schutz vor gefälschten Anfragen. | Sitzung |
| `Identity.External` und Cookies des Discord-Anmeldevorgangs | Nur während der Anmeldung mit Discord. | Minuten |
| `Identity.TwoFactorUserId` | Zwischenschritt bei der Zwei-Faktor-Anmeldung. | Minuten |
| `__Secure-ember.merge` | Hält einen angefragten Kontoabgleich mit Discord für 15 Minuten. | 15 Minuten |
| `__Secure-ember.discord-link` | Hält beim Verknüpfen mit Discord die Anfrage fest. Nur über HTTPS, für JavaScript nicht lesbar. | 5 Minuten |

Außerdem speichern die Web-App und die Android-App einige Dinge auf dem Gerät. Die Liste zeigt, was wo liegt und wann es weg ist:

| Wo | Name | Was drin steht | Wann es gelöscht wird |
|---|---|---|---|
| Browser, Local Storage | `ember.mode` | Ob du mit oder ohne Konto nutzt | Wenn du den Modus wechselst oder die Website-Daten löschst |
| Browser, Local Storage | `ember.theme`, `ember.accountTheme` | Dein gewähltes Design und das Design, das dein Konto zuletzt hatte | Beim Abmelden oder wenn du die Website-Daten löschst |
| Browser, Local Storage | `ember.colorScheme` | Hell, dunkel oder System. Nur auf diesem Gerät, nicht im Konto | Wenn du „System“ wählst oder die Website-Daten löschst |
| Browser, Local Storage | `ember.calm` | Ob der Ruhige Modus auf diesem Gerät an ist (Kopie der Einstellung) | Wenn du ihn änderst oder die Website-Daten löschst |
| Browser, Local Storage | `ember.language` | Die Sprache, die du auf diesem Gerät gewählt hast | Wenn du sie änderst oder die Website-Daten löschst |
| Browser, Local Storage | `ember.signupEmail` | E-Mail-Adresse aus der Anmeldung, bis die Bestätigung abgeschlossen ist | Nach der Bestätigung, beim Abmelden, spätestens nach einem Tag |
| Browser, Local Storage | `ember.staySignedIn` | Kennung deines Kontos. Nur, wenn du bei der Anmeldung mit E-Mail und Passwort „Angemeldet bleiben“ gewählt hast | Beim Abmelden |
| Browser, Local Storage | `ember.offlineMe` | Kopie deines Kontostands: E-Mail-Adresse, Zeitzone, Design, Stand der Einwilligung und des Tagebuchs. Nur mit „Angemeldet bleiben“ | Beim Abmelden oder wenn die Sitzung abläuft. Die Kopie wird nur am selben Tag genutzt |
| Browser, Local Storage | `ember.momentVoice` | Ob „Ich brauche einen Moment“ dich per Stimme führt | Wenn du es änderst oder die Website-Daten löschst |
| Browser, Local Storage | `ember.dismissedNotices` | Kennungen der Hinweise, die du geschlossen hast | Wenn du die Website-Daten löschst |
| Browser, Local Storage | `ember.tips.<Kennung>` | Mit der Kennung deines Kontos im Namen: wann du welchen Tipp gesehen hast und welche Tipps noch anstehen | Wenn du die Website-Daten löschst |
| Browser, Local Storage | `ember.browserReminderNames`, `ember.browserReminderNamesFor` | Ob dieser Browser Habit-Namen in Erinnerungen zeigen soll, und für welches Konto | Wenn du die Einstellung änderst oder die Website-Daten löschst |
| Browser und Android-App, Local Storage | `ember.seenReleaseNotes` | Die letzte Version, deren Neuigkeiten du auf diesem Gerät gesehen hast, damit der Hinweis zu einer neuen Version auf „Heute“ nur einmal erscheint | Wenn du die Website-Daten oder die Daten der App löschst. Beim Abmelden bleibt der Eintrag |
| Browser und Android-App, Local Storage | `ember.tzClaimed.<Kennung>` | Mit der Kennung deines Kontos im Namen: Merker, dass die Zeitzone dieses Geräts einmal in dein Konto übernommen wurde, weil dort noch keine eingestellt war | Wenn du die Website-Daten oder die Daten der App löschst. Beim Abmelden bleibt der Eintrag |
| Browser, Session Storage | `ember.preloadReload` | Zeitpunkt des letzten Neuladens nach einem App-Update | Am Ende der Browser-Sitzung |
| Browser, IndexedDB `ember-device` | Speicher `cache` | Kopie deiner Habit-Liste. Nur mit „Angemeldet bleiben“ | Beim Abmelden oder wenn die Sitzung abläuft |
| Browser, IndexedDB `ember-device` | Speicher `edits` | Änderungen, die du offline gemacht hast und noch nicht gesendet sind | Sobald sie gesendet sind. Beim Abmelden werden die offenen Änderungen verworfen |
| Browser, IndexedDB `ember-journal` | Speicher `keys` | Tagebuchschlüssel, nur mit „Auf diesem Gerät merken“. Für JavaScript nicht auslesbar | Beim Abmelden, beim Ausschalten des Tagebuchs oder wenn du ihn auf diesem Gerät vergisst |
| Browser, IndexedDB `ember-journal` | Speicher `held` | Tagebuch-Änderungen, die warten, weil das Tagebuch gerade gesperrt ist. Sie liegen dort unverschlüsselt | Sobald sie übertragen sind, beim Abmelden |
| Browser, IndexedDB `ember-local` | Speicher `data` | Alle Daten ohne Konto: Habits, Check-ins, Ausrutscher, Tiefs, Routinen, Tagesnotizen, Stimmungen und Einstellungen | Wenn du die Daten auf diesem Gerät löschst. Nach dem Hochladen in ein Konto, sobald die Übertragung bestätigt ist |
| Browser, Cache Storage | Dateien der Web-App, Schriften (`ember-fonts`) und die Sprache für Benachrichtigungen des Service Workers (`ember-language`) | Damit die App auch ohne Verbindung startet. Die Sprache ist die auf diesem Gerät gewählte | Wenn du die Website-Daten löschst. Neue Versionen ersetzen die Dateien |
| Android-App, Local Storage | `ember.reminderPlanned` | Prüfsumme des 14-Tage-Erinnerungsplans | Beim Abmelden und wenn der Plan neu geschrieben wird |
| Android-App, Local Storage | `ember.reminderIssued` | Erinnerungen, die schon ausgelöst wurden: Konto-Kennung, Habit-Nummer und Datum | Beim Abmelden |
| Android-App, Local Storage | `ember.installId` | Zufällige Kennung dieser Installation, mit der sich das Telefon als Erinnerungsziel anmeldet | Wenn du die App deinstallierst oder ihre Daten löschst |
| Android-App, Local Storage | `ember.reminderPhone` | Kennung dieses Telefons als Erinnerungsziel und ob es eingeschaltet ist, für Erinnerungen ohne Internet | Beim Abmelden |
| Android-App, Local Storage | `ember.reminderNames` | Einstellung „Namen der Habits in Erinnerungen zeigen“ (aus, wenn nicht gewählt) | Beim Abmelden, dann wieder aus |
| Android-App, Local Storage | `ember.exactAlarmAsked` | Merker, dass die Beta-App die Einstellung „Alarme und Erinnerungen“ schon einmal geöffnet hat, damit Erinnerungen pünktlich kommen | Wenn du die App deinstallierst oder ihre Daten löschst |
| Android-App, Widget | Heutige und morgige Habits für das Widget (Namen und Stand) | App-eigener Speicher, nicht verschlüsselt. Nur die App liest ihn, und die Sicherung des Telefons nimmt ihn nicht mit | Beim Abmelden |
| Android-App, Widget | Schlüssel für Check-ins aus dem Widget | Verschlüsselt im Android-Keystore. Der Server speichert nur einen Hash davon. Ein neuer Schlüssel ersetzt den alten | Beim Abmelden. Der Schlüssel gilt nur für die Anmeldesitzung und endet mit ihr |
| Android-App, Widget | Check-ins aus dem Widget, die die App noch nicht übertragen hat | App-eigener Speicher | Sobald sie übertragen sind, beim Abmelden |
| Android-App, sicherer Speicher (Keystore) | Anmeldetoken und, falls gemerkt, der Tagebuchschlüssel | Anmeldung und Tagebuch | Beim Abmelden |

Beim Abmelden meldet die Web-App das Gerät beim Server ab und entfernt das Push-Abo dieses Geräts. Die Speicherung im Browser und in der App ist für Anmeldung, Schutz und die Funktionen nötig, die du nutzt. Eine Einwilligung dafür brauchen wir nach § 25 Abs. 2 Nr. 2 TDDDG nicht.

### 3.9 Ohne Konto: Daten nur auf dem Gerät

Wenn du Ember ohne Konto nutzt, bleiben deine Daten auf deinem Gerät: Habits, Check-ins, Ausrutscher, Tiefs, Routinen, Tagesnotizen, Stimmungen und Einstellungen. Sie gehen nicht an den Server, nicht an Discord und nicht an Push-Dienste. Nur Feedback, das du selbst abschickst, geht an den Server (Abschnitt 3.10).

Auch ohne Konto fragt die App den Server nach Service-Hinweisen: beim Start, alle 10 Minuten, solange die App offen ist, und wenn du zurückkehrst. Die Android-App prüft außerdem, ob es eine neuere Version gibt. Bei diesen Anfragen geht keine Angabe aus deinen Daten an den Server. Sichtbar sind deine IP-Adresse und deine Browser- oder App-Kennung (User-Agent). Mehr dazu in Abschnitt 6.2.

Der Browser oder das Betriebssystem kann die Daten löschen, zum Beispiel wenn du die Website-Daten entfernst, die App deinstallierst oder Safari eine Website nach sieben Tagen ohne Besuch aufräumt. Mit „Backup exportieren“ speicherst du eine Datei mit deinen Daten.

Legst du später ein Konto an, lädt die App deine Daten hoch. Die lokale Kopie löscht sie erst, wenn alle Schritte erfolgreich waren.

### 3.10 Feedback

Über „Feedback senden“ in den Einstellungen der Web-App und der App, auf der Seite `/feedback` der Web-App oder mit `/feedback` im Discord-Bot kannst du uns eine Nachricht schicken. Ember speichert dazu:

- den Titel (höchstens 100 Zeichen) und den Text, den du schreibst (höchstens 2000 Zeichen),
- bis zu 5 Bilder, zusammen höchstens 10 MB, wenn du welche anhängst (in der Web-App und der App nur angemeldet),
- über welchen Weg es kam (Web-App, Android-App oder Discord),
- die Version der App, wenn sie bekannt ist,
- den Zeitpunkt,
- die Verknüpfung mit deinem Konto, wenn du angemeldet bist. So können wir dich bei Rückfragen direkt erreichen. Mit „Feedback anonym senden“ speichert Ember das Feedback ohne Bezug zu deinem Konto, ebenso ohne Konto, abgemeldet oder in Discord ohne Ember-Konto. Der Text oder ein Bild kann dich trotzdem erkennbar machen.

Deine Habits, Check-ins, Tagesnotizen und anderen Daten schickt Ember dabei nicht mit. Auch ohne Konto geht nur das Feedback an den Server; deine Daten auf dem Gerät bleiben dort. Im Discord-Bot laufen Text und Bilder über Discord (Abschnitt 6.1); Ember lädt die Bilder dort herunter. Bitte schreib nichts ins Feedback, was du nicht teilen möchtest.

**Gesundheitsangaben im Feedback.** Am Formular steht dieser Hinweis: „Wenn dein Feedback oder ein Bild Angaben zu deiner Gesundheit enthält, willigst du mit dem Absenden ausdrücklich ein, dass der Betreiber sie zur Bearbeitung deines Feedbacks verarbeitet (Art. 9 Abs. 2 lit. a DSGVO). Du kannst das jederzeit per E-Mail an contact@nicowoellnitz.de widerrufen.“

**Bilder.** Ember nimmt nur JPEG, PNG und WebP an. Die Web-App und die App verkleinern Bilder schon auf deinem Gerät. Bevor Ember ein Bild speichert, entfernt der Server die Metadaten, zum Beispiel EXIF-Angaben wie Aufnahmeort (GPS), Aufnahmezeit und Kameramodell. Wir nutzen die Bilder nur, um dein Feedback zu bearbeiten und Ember zu verbessern. Sie bleiben gespeichert, bis das gemeldete Problem erledigt ist, und werden 30 Tage danach gelöscht, spätestens aber 12 Monate nach dem Eingang. Der Betreiber sieht die Bilder in seinem Feedback-Board (Abschnitt 6.6). Bildschirmfotos aus Ember können Tagesnotizen, Stimmungen oder Habit-Namen zeigen. Schau deshalb vor dem Senden, ob darauf etwas zu sehen ist, das du nicht teilen möchtest, auch Angaben über andere Menschen.

Wir nutzen das Feedback nur, um Ember zu verbessern. Einmal am Tag schickt Ember dem Betreiber über Resend (Abschnitt 6.3) eine E-Mail, die nur sagt, wie viele neue Rückmeldungen es gibt und wann sie eingegangen sind. Titel, Text, Bilder, Bildkennungen, Weg und App-Version stehen nicht darin. Das Feedback selbst liest der Betreiber nur im Feedback-Board (Abschnitt 6.6). Schlägt der Versand fehl, schickt Ember dem Betreiber eine Fehlermeldung ohne Inhalte, mit der Zahl und dem Alter der offenen Rückmeldungen. Ältere Feedback-E-Mails aus der Zeit vor dieser Fassung enthielten noch Titel und Text. Diese Kopien liegen im Postfach des Betreibers. Weil die E-Mails keine Konto-ID enthalten, werden sie bei einer Kontolöschung nicht einzeln gelöscht (Abschnitt 7). Der Betreiber kann im Feedback-Board private Notizen zu einem Feedback schreiben und dessen Status setzen (Abschnitt 6.6). Ember löscht Feedback nach 12 Monaten automatisch. Ist es mit deinem Konto verknüpft, löscht Ember es zusammen mit dem Konto, auch früher (Abschnitt 7). Rechtsgrundlage ist Art. 6 Abs. 1 lit. f DSGVO, und bei Gesundheitsangaben zusätzlich Art. 9 Abs. 2 lit. a DSGVO. Unser berechtigtes Interesse ist, Fehler zu finden und Ember so weiterzuentwickeln, wie es den Menschen hilft, die es nutzen. Du kannst widersprechen (Art. 21 DSGVO, Abschnitt 9).

### 3.11 Was Ember nicht verarbeitet

- Nachrichteninhalte aus Discord. Der Bot liest keine Nachrichten mit und nutzt keine privilegierten Gateway-Intents.
- Mitgliederlisten, Online-Status und Freundeslisten.
- Standortdaten, Kontakte, Telefonnummern und Zahlungsdaten. Ember hat keine Bezahlfunktion.
- Werbe-Kennungen und Geräte-Fingerabdrücke.
- Analyse- oder Absturzberichts-Bibliotheken wie Firebase in der Android-App.

### 3.12 Momente („Ich brauche einen Moment“)

Der Ablauf „Ich brauche einen Moment“ begleitet dich durch einen schwierigen Moment: innehalten, benennen, eine kurze Übung machen und danach optional etwas festhalten. Er läuft auf deinem Gerät:

- **Auf dem Gerät.** Was du auswählst (Gefühl, Stärke, Übung und Schritte) bleibt im Arbeitsspeicher deines Geräts. Ember sendet es nicht an den Server, schreibt es nicht in den Browser oder die App (weder in den Local Storage noch in IndexedDB) und legt dafür keinen Eintrag an. Verlässt du den Ablauf oder lädst du die Seite neu, ist es weg.
- **Ohne Konto.** Der Ablauf braucht kein Konto und keine Verbindung zum Server, sobald die App geladen ist.
- **Krisennummern.** Die Nummern sind fest in der App eingebaut. Ember fragt sie nicht ab. Welche Nummern erscheinen, richtet sich nach dem Land, das du auswählst, oder nach Sprache und Zeitzone deines Geräts. Deine Auswahl gilt nur für diese Sitzung und wird nicht gespeichert.
- **Anrufen.** Ein Tipp auf eine Nummer öffnet die Telefon-App deines Geräts. Das Gespräch läuft zwischen dir und dem Dienst beziehungsweise deinem Telefonanbieter. Ember stellt die Verbindung nicht her und bekommt davon nichts mit.
- **Android-Verknüpfungen.** Das Momente-Widget und die Verknüpfung auf dem Startbildschirm enthalten keine Daten. Das Widget zeigt nur seine Beschriftung und öffnet den Ablauf. Für das Heute-Widget gilt weiter Abschnitt 3.8.
- **Reflexion.** Eine Reflexion ist optional. Ember speichert sie nur, wenn das private Tagebuch eingeschaltet und entsperrt ist und du der Datenschutzerklärung zugestimmt hast (Abschnitt 4). Dann landet sie im privaten Tagebuch, als Teil der Tagesnotiz für diesen Tag. Dein Gerät verschlüsselt sie, bevor sie zum Server geht. Der Server sieht nur die verschlüsselte Tagesnotiz. Ohne eingeschaltetes privates Tagebuch speichert Ember keine Reflexion. Sie bleibt dann nur auf dem Bildschirm.
- **Deine Momente.** Die Seite „Deine Momente“ baut Ember auf deinem Gerät aus den entschlüsselten Tagesnotizen auf. Dafür gibt es auf dem Server keine eigene Tabelle. Wählst du bei einer Reflexion „hat geholfen“ und eine Übung, steht das als Eintrag im Text der Tagesnotiz.
- **Sprachführung.** Ob die Sprachführung an ist, merkt sich der Browser auf diesem Gerät (Abschnitt 3.8).
- **Tagebuch ausschalten.** Schaltest du das private Tagebuch aus, lädt dein Gerät auch die Reflexionen im Klartext hoch, wie alle anderen Einträge (Abschnitt 3.4).

Ember wertet nicht aus, was du schreibst. Es gibt keine automatische Auswertung von Texten, keine Erkennung von Krisen und keine KI. Die Übung richtet sich nach dem Gefühl, das du angibst, nicht nach dem, was du schreibst.

Eine Reflexion kann Angaben zu deiner Gesundheit enthalten, zum Beispiel wie es dir gerade geht. Dafür gelten die Regeln für Tagesnotizen in Abschnitt 4. Der Ablauf selbst speichert nichts und braucht deshalb keine eigene Einwilligung.

### 3.13 Am Lagerfeuer (Fireside)

Am Lagerfeuer verbindest du dich mit Menschen, denen du vertraust, und ihr schickt euch ab und zu Wärme. Die Funktion ist standardmäßig aus. Du schaltest sie in den Einstellungen selbst ein und wählst dort deinen Namen. Vorher fragt Ember auf einer kurzen eigenen Seite nach deiner ausdrücklichen Einwilligung (siehe Rechtsgrundlage unten). Dafür speichert Ember:

| Daten | Wofür |
|---|---|
| Ob Am Lagerfeuer an ist, Zeitpunkt und Version deiner Einwilligung dafür, dein Name dort (1 bis 32 Zeichen) und ob du Benachrichtigungen willst. Schaltest du es aus, bleibt dein Name gespeichert, damit du ihn beim nächsten Einschalten nicht neu eingeben musst. | Damit andere dich finden und Ember dich benachrichtigen kann. |
| Deine Bitte um Wärme für heute. Sie gilt bis zur nächsten Mitternacht in deiner Zeitzone. | Damit die Menschen am Lagerfeuer sehen, dass du heute um Wärme bittest. |
| Verbindungen zu anderen Menschen: Stummschaltung, ob du deinen Check-in-Status für diese Person freigibst, und Umbenennungen. Höchstens 20 Verbindungen. | Damit ihr euch gegenseitig am Lagerfeuer seht. |
| Einmal-Codes: Ember speichert nur einen Hash des Codes und zusätzlich eine verschlüsselte Fassung, damit du den Code noch einmal ansehen kannst. Ein Code gilt 30 Minuten und wird benutzt oder gelöscht. Ein neuer Code ersetzt den alten. | Damit eine Person sich mit deinem Code verbinden kann. |
| Fehlversuche beim Eingeben eines Codes: Zahl und Beginn des aktuellen Stundenfensters, in deinem Konto | Schutz gegen Raten. Nach 10 Fehlversuchen in einer Stunde nimmt Ember keine Eingaben mehr von dir an, bis die Stunde vorbei ist. |
| Wärme-Nachrichten: eine von fünf festen Nachrichten, Absender, Empfänger, Zeitpunkt, ob der Empfänger sie weggeklickt hat und ob noch eine Benachrichtigung aussteht | Damit ihr euch Wärme schicken könnt. Nach 30 Tagen löscht Ember sie. |

Die fünf Nachrichten sind fest: „Ich denk an dich“, „Ich bin stolz auf dich“, „Sei heute sanft zu dir“, „Du bist nicht allein“ und „Ich schick dir eine warme Decke“. Freitext gibt es dort nicht.

**Was die Menschen am Lagerfeuer sehen.** Eine verbundene Person sieht deinen Namen, ob du heute um Wärme gebeten hast (nur bis Mitternacht) und ob du heute eingecheckt hast. Den Check-in-Status sieht sie nur, wenn du ihn für genau diese Person freigegeben hast. Sie sieht außerdem, wenn du deinen Namen änderst. Sie sieht nicht deine Habits, Stimmungen, Tagesnotizen, dein Tagebuch oder deine Momente.

**Benachrichtigungen.** Ember benachrichtigt die Menschen am Lagerfeuer über Wärme und Umbenennungen, per Discord-Direktnachricht oder per Web-Push. In der Discord-Nachricht steht dein Name. Im Web-Push steht er nur, wenn der Browser Namen zeigen darf (Abschnitt 3.5).

**Löschen.** Schaltest du Am Lagerfeuer aus, endet deine Einwilligung dafür, und Ember entfernt alle deine Verbindungen, Codes und Wärme-Nachrichten in beide Richtungen. Entfernst du eine Person, löscht Ember die Verbindung und alle Wärme-Nachrichten zwischen euch. Mit deinem Konto löscht Ember alles davon.

**Export.** Im Export (Abschnitt 9) stehen deine Einstellungen für Am Lagerfeuer: an oder aus, Name und Benachrichtigungen. Verbindungen, Codes und Wärme-Nachrichten sind nicht im Export.

**Rechtsgrundlage.** Für Name, Verbindungen, Codes und Wärme-Nachrichten gilt Art. 6 Abs. 1 lit. b DSGVO. Deine Bitte um Wärme und die Freigabe deines Check-in-Status sagen etwas über deinen Zustand und können Gesundheitsdaten sein. Dafür holt Ember beim Einschalten von Am Lagerfeuer deine ausdrückliche Einwilligung ein (Art. 9 Abs. 2 lit. a DSGVO) und speichert Zeitpunkt und Version. Du widerrufst sie jederzeit, indem du Am Lagerfeuer ausschaltest oder die Freigabe für eine Person zurücknimmst. Diese Einwilligung ist getrennt von der Einwilligung nach Abschnitt 4.

## 4. Gesundheitsdaten (Art. 9 DSGVO)

Viele Habits haben mit Gesundheit zu tun, zum Beispiel „Medikamente nehmen“, „Schlafen“ oder „Sport“. Ember bietet einige davon sogar als Vorlage an. Aus Habit-Namen, Check-ins, Tiefs, Tagesnotizen, Habit-Notizen, Stimmungen, Ausrutschern mit Gründen und „Was mir hilft“ lässt sich deshalb unter Umständen etwas über deine Gesundheit ablesen. Solche Daten sind nach Art. 9 DSGVO besonders geschützt.

Wir behandeln deshalb **alle** Habit-Namen, Check-ins, Tiefs, Tagesnotizen, Habit-Notizen, Stimmungen, Ausrutscher mit Gründen und die Liste „Was mir hilft“ so, als könnten sie Gesundheitsdaten sein. Ember speichert sie nur mit deiner **ausdrücklichen Einwilligung** (Art. 9 Abs. 2 lit. a DSGVO). Routinen-Namen, das Umbenennen von Habits und Push-Abos brauchen bisher keine Einwilligung. Für Am Lagerfeuer fragt Ember eine eigene Einwilligung ab (Abschnitt 3.13).

### So fragt Ember nach deiner Einwilligung

- **Neue Nutzer:** Bevor du dein erstes Habit anlegst (auch aus einer Vorlage), einen Check-in machst, einen Ausrutscher, ein Tief, eine Tagesnotiz, eine Habit-Notiz oder eine eigene Stimmung speicherst, eine Erinnerung als erledigt markierst, die Liste „Was mir hilft“ anlegst, Daten importierst oder Konten zusammenführst, fragt Ember dich, ob das für dich in Ordnung ist. Die Abfrage nennt, welche Daten das sind, welche Dienste sie erreichen (Cloudflare, Discord), dass der Betreiber technisch Zugriff auf den Server hat und wie du widerrufst. In der Web-App und der App ist es eine eigene Seite: Du bestätigst mit einem Häkchen, dass du mindestens 16 Jahre alt bist, und antwortest mit „Ich stimme zu“ oder „Nein danke“. Im Discord-Bot antwortest du mit den Schaltflächen „Ich stimme zu, ich bin mindestens 16“ oder „Nein danke“. Die Texte dieser Abfrage gibt es auf Deutsch und Englisch, je nach der Sprache, die du in Ember gewählt hast. Erst nach deiner Zustimmung speichert Ember diese Daten.
- **Bestehende Nutzer ohne Einwilligung:** Hast du Ember schon genutzt und nie zugestimmt, gilt für dich ab dieser Fassung dasselbe wie nach „Nein danke“: Ember legt nichts Neues an und schickt keine Erinnerungen mehr, deine Daten kannst du nur noch exportieren, und die 30 Tage beginnen mit dem Erscheinen dieser Fassung. Vor der Löschung erinnert dich Ember daran.
- **Bestehende Nutzer mit Einwilligung zu einer früheren Fassung:** Ember fragt dich noch einmal, in der Web-App einmal pro Sitzung und im Discord-Bot höchstens einmal am Tag. Bis du antwortest, bleiben deine vorhandenen Daten gespeichert und die Erinnerungen laufen weiter, weil deine frühere Einwilligung sie abdeckt. Neue Habits, Check-ins, Tiefs, Ausrutscher, Tagesnotizen, Habit-Notizen, Stimmungen und „Was mir hilft“ gehen erst nach deiner Zustimmung zu dieser Fassung.
- **Wenn du „Nein danke“ wählst:** Ember legt nichts Neues an und schickt keine Erinnerungen mehr. Vorhandene Daten bleiben nur noch für Export und Löschung erreichbar. Wenn du innerhalb von 30 Tagen nicht zustimmst, löscht Ember die Daten nach Abschnitt 4. Vorher erinnert dich Ember daran, 3 Tage vor der Löschung per Discord-Direktnachricht und Web-Push, soweit du diese Ziele nutzt. Der Discord-Bot bietet dir die Löschung sofort an. Stimmst du innerhalb der 30 Tage doch zu, löscht Ember nichts. Dein Konto und deine Einstellungen bleiben. Der Discord-Bot fragt dich 30 Tage lang nicht von sich aus.
- **Wenn sich diese Erklärung ändert:** Mit dieser Fassung fragt Ember alle Nutzer noch einmal, auch wenn sie vorher zugestimmt haben. Bei einer weiteren neuen Version geschieht das auf dieselbe Weise.

Wenn du zustimmst, speichert Ember den Zeitpunkt und die Version dieser Erklärung (Abschnitt 3.1).

### Tagesnotizen, Habit-Notizen, Stimmungen und Tiefs sind nicht sichtbar für andere Nutzer

Tagesnotizen, Habit-Notizen und Stimmungen erscheinen nie im Discord-Bot, in Benachrichtigungen, in der Wochenübersicht oder im Export des Discord-Bots (`/export`). Ein Tief erscheint auf einer Streak-Karte, die du mit `/habit share` postest, nur als normale Lücke.

### Widerruf

Du kannst deine Einwilligung jederzeit so einfach widerrufen, wie du sie gegeben hast (Art. 7 Abs. 3 DSGVO): in der Web-App und der App in den Einstellungen unter „Datenschutz“ mit „Einwilligung widerrufen“, im Discord-Bot mit `/privacy withdraw`. Danach speichert Ember keine neuen Daten nach Abschnitt 4 mehr, und die Erinnerungen hören sofort auf, auch die, die die App auf deinem Handy plant, sobald sie den Server erreicht. Du kannst deine vorhandenen Daten exportieren. Löschst du sie nicht selbst, löscht Ember sie nach 30 Tagen. 3 Tage vorher erinnert dich Ember daran, per Discord-Direktnachricht und Web-Push, soweit du diese Ziele nutzt. Stimmst du vorher wieder zu, löscht Ember nichts. Dein Konto und deine Einstellungen bleiben. Auch das Löschen des Kontos widerruft die Einwilligung (in den Einstellungen unter „Dein Konto löschen“ oder im Discord-Bot mit `/privacy delete`). Was bis zum Widerruf passiert ist, bleibt rechtmäßig. Nach der Löschung bleiben noch Kopien auf anderen Geräten, in Backups, in technischen Protokollen mit Nutzer-IDs bis zu deren Rotation und bei Dienstleistern (Abschnitt 9).

## 5. Zwecke und Rechtsgrundlagen

| Zweck | Rechtsgrundlage |
|---|---|
| Konto, Anmeldung, Sitzungen, Sperre nach Fehlversuchen, Bestätigung der E-Mail-Adresse, Zurücksetzen des Passworts, Zwei-Faktor-Anmeldung, Verknüpfung mit Discord | Art. 6 Abs. 1 lit. b DSGVO (Nutzungsvertrag, siehe Nutzungsbedingungen). Für die Sicherheit des Kontos zusätzlich Art. 6 Abs. 1 lit. f DSGVO. |
| Habits, Check-ins, Tiefs, Ausrutscher, Gnadentage, Streaks, deine eigenen Statistiken, Erinnerungen, Wochenübersicht, Export und Löschung | Art. 6 Abs. 1 lit. b DSGVO. Für Habit-Namen, Check-ins, Tiefs und Ausrutscher zusätzlich Art. 9 Abs. 2 lit. a DSGVO (Einwilligung). |
| Tagesnotizen, Habit-Notizen, Stimmungen und „Was mir hilft“ | Art. 6 Abs. 1 lit. b DSGVO und Art. 9 Abs. 2 lit. a DSGVO (Einwilligung). |
| Momente: Ablauf auf dem Gerät | Keine Verarbeitung durch Ember. Eine gespeicherte Reflexion landet als Teil der Tagesnotiz und folgt den Regeln für Tagesnotizen (Abschnitt 3.12). |
| Routinen, Umbenennen von Habits, Push-Abos | Art. 6 Abs. 1 lit. b DSGVO. Keine Einwilligung bisher. |
| Erinnerungen über Discord, Web-Push und lokal in der App, Erinnerungsziele, Knopf „Erledigt“ auf Erinnerungen | Art. 6 Abs. 1 lit. b DSGVO. Jedes Ziel schaltest du selbst ein oder aus. |
| Am Lagerfeuer: Einstellungen, Name, Verbindungen, Codes und Wärme-Nachrichten | Art. 6 Abs. 1 lit. b DSGVO (Abschnitt 3.13). |
| Am Lagerfeuer: Bitte um Wärme und Freigabe des Check-in-Status | Art. 9 Abs. 2 lit. a DSGVO (eigene Einwilligung beim Einschalten, Abschnitt 3.13). |
| Löschung nach Widerruf oder „Nein danke“ und die Erinnerung davor (Abschnitt 4) | Art. 6 Abs. 1 lit. c DSGVO in Verbindung mit Art. 17 Abs. 1 lit. b DSGVO. Für die Erinnerung davor Art. 6 Abs. 1 lit. f DSGVO. Unser berechtigtes Interesse ist, dass du nicht ohne Vorwarnung Daten verlierst. |
| E-Mails zur Bestätigung und zum Zurücksetzen des Passworts | Art. 6 Abs. 1 lit. b DSGVO. Weil die Adresse auch einer anderen Person gehören kann, zusätzlich Art. 6 Abs. 1 lit. f DSGVO. Unser berechtigtes Interesse ist, dass nur der Inhaber einer Adresse ein Konto damit anlegen kann. |
| Hinweis-E-Mail an eine Adresse, die schon ein Konto hat (Abschnitt 6.3) | Art. 6 Abs. 1 lit. f DSGVO. Unser berechtigtes Interesse ist der Schutz der Konten. [[Nico: Die Bestätigungs- und Hinweis-E-Mails enthalten bisher keinen Link zu dieser Datenschutzerklärung. Einen kurzen Link ergänzen (Art. 14 DSGVO)?]] |
| Feedback, die tägliche Feedback-E-Mail an den Betreiber (nur Zahl und Zeitpunkte) und das Feedback-Board mit Notizen (Abschnitt 3.10, 6.6) | Art. 6 Abs. 1 lit. f DSGVO. Unser berechtigtes Interesse ist, Ember zu verbessern. Bei Gesundheitsangaben im Feedback zusätzlich Art. 9 Abs. 2 lit. a DSGVO (Hinweis am Formular, Abschnitt 3.10). |
| Nutzungszahlen des Betreibers aus Anmelde- und Nutzungszeiten (Abschnitt 6.6) | Art. 6 Abs. 1 lit. f DSGVO. Unser berechtigtes Interesse ist, Ember zu betreiben und weiterzuentwickeln. Keine Gesundheitsdaten, keine Einwilligung nötig. Du kannst widersprechen (Abschnitt 9). |
| Hinweise (Service-Mitteilungen) und Update-Prüfung über den Server und den Cloudflare-Worker (Abschnitt 6.2) | Art. 6 Abs. 1 lit. f DSGVO. Unser berechtigtes Interesse ist, dir wichtige Hinweise zu zeigen. Auf dem Gerät speichert die App nur, welche Hinweise du geschlossen hast. |
| Widget-Check-in-Schlüssel (Abschnitt 3.8) | Art. 6 Abs. 1 lit. b DSGVO für die Funktion, Art. 6 Abs. 1 lit. f DSGVO für die Sicherheit. |
| Einwilligungsnachweis (auch Widerruf und Einwilligung für Am Lagerfeuer) | Art. 6 Abs. 1 lit. c DSGVO in Verbindung mit Art. 7 Abs. 1 DSGVO |
| Technische Protokolle, Ratenbegrenzung, Fehlersuche, Sicherheit, Backups | Art. 6 Abs. 1 lit. f DSGVO. Unser berechtigtes Interesse ist ein sicherer, stabiler Betrieb und dass deine Daten nach einem technischen Ausfall nicht verloren gehen. |
| Cookies und Speicher im Browser und in der App (Abschnitt 3.8) | § 25 Abs. 2 Nr. 2 TDDDG. Die Verarbeitung der Daten selbst richtet sich nach den Zeilen oben. |

Es gibt keine automatisierte Entscheidungsfindung und kein Profiling im Sinne von Art. 22 DSGVO. Streaks und deine eigenen Statistiken sind einfache Berechnungen, die nur dir angezeigt werden und keine rechtliche oder ähnlich erhebliche Wirkung haben. Die Nutzungszahlen des Betreibers (Abschnitt 6.6) sind Summen und werden nicht auf einzelne Personen angewendet.

## 6. Wer bekommt deine Daten?

### 6.1 Discord

Ember läuft über Discord. Was du im Bot tust und was Ember dir schickt (Antworten, Erinnerungen, Wochenübersichten, Exporte), läuft über die Server von Discord. Discord verarbeitet diese Daten als eigener Verantwortlicher nach seinen eigenen Regeln. Mit Discord hast du einen eigenen Vertrag. Discord ist kein Auftragsverarbeiter von Ember.

- Anbieter: Discord Inc., USA.
- Datenschutzerklärung von Discord: https://discord.com/privacy
- Drittlandübermittlung: Discord verarbeitet Daten auch in den USA. Weil Discord eigener Verantwortlicher ist, stützt Discord die Übermittlung auf seine eigene Grundlage, nach eigenen Angaben auf das EU-US Data Privacy Framework und Standardvertragsklauseln. [[Nico: Aktuelle DPF-Teilnahme von Discord prüfen]]
- Erinnerungen: Nutzt du Discord als Erinnerungsziel, bekommt Discord die Erinnerungen mit den Namen deiner Habits.

Die Verbindung zwischen dir und Discord ist verschlüsselt (TLS).

**Bitte beachte:** Direktnachrichten von Ember, Streak-Karten aus `/habit share` und Nachrichten in einem Kanal liegen bei Discord. Ember kann sie nach dem Senden nicht zurückholen, auch nicht mit `/privacy delete`. Moderatoren eines Servers können Streak-Karten in ihrem Server löschen.

### 6.2 Hosting und öffentlicher Zugang

Ember läuft auf einem Server in Deutschland, den der Verantwortliche selbst betreibt, zu Hause. Es gibt keinen Dienstleister, der den Server betreibt oder Zugriff auf die Datenbank hat.

Die Web-App und die API sind öffentlich über einen Cloudflare Tunnel erreichbar. Dabei gilt:

- **Cloudflare, Inc.**, USA, ist Auftragsverarbeiter nach Art. 28 DSGVO. Cloudflare nimmt die Verbindungen aus dem Internet entgegen und leitet sie über den Tunnel an unseren Server weiter.
- Cloudflare beendet die TLS-Verschlüsselung. Dadurch kann Cloudflare die Inhalte der Anfragen sehen, die über den Tunnel laufen, also auch deine Eingaben in der Web-App. Cloudflare sieht außerdem deine IP-Adresse und technische Verbindungsdaten.
- Cloudflare verarbeitet Daten auch in den USA. Die Übermittlung stützt sich auf das EU-US Data Privacy Framework und, soweit es nicht greift, auf Standardvertragsklauseln. Mit Cloudflare besteht ein Auftragsverarbeitungsvertrag (DPA).
- Vor der Web-App läuft außerdem ein kleines Programm bei Cloudflare (Worker). Es leitet Anfragen unverändert an unseren Server weiter. Ist der Server nicht erreichbar, liefert es eine gespeicherte Kopie der Web-App und eine Wartungsmeldung für die Schnittstelle. Ember speichert dort nichts. Ember hat keine eigene Protokollierung eingerichtet. Cloudflare protokolliert technische Verbindungsdaten nach eigenen Regeln.
- Die Hinweisdatei, die Apps abrufen, enthält keine personenbezogenen Daten. Ihr Abruf geht über Cloudflare und zeigt dem Server und Cloudflare deine IP-Adresse und deine Browser- oder App-Kennung.
- Die Datenbank, der Discord-Bot und Ember selbst bleiben auf dem eigenen Server.
- Der Zugang des Betreibers zu seinem Dashboard läuft über Tailscale, ein verschlüsseltes privates Netz. Tailscale vermittelt nur diesen verschlüsselten Verbindungsweg und sieht technische Verbindungsdaten der Geräte des Betreibers, keine Inhalte.

### 6.3 E-Mail-Versand

Ember verschickt E-Mails über den Dienst **Resend**. Vertragspartner ist **Plus Five Five, Inc.** (Resend), 2261 Market Street #5039, San Francisco, CA 94114, USA. Resend verarbeitet die E-Mails als Auftragsverarbeiter nach Art. 28 DSGVO. Mit der Anmeldung bei Resend gilt dessen Auftragsverarbeitungsvertrag (DPA) auch für uns.

Ember verschickt diese E-Mails:

- Bestätigung der E-Mail-Adresse mit einem Code und einem Link,
- Zurücksetzen des Passworts mit einem Code oder einem Link,
- Hinweis an eine Adresse, die schon ein Konto hat, wenn jemand sich mit ihr registrieren will. Die Mail enthält Links zum Anmelden und zum Zurücksetzen des Passworts. Sie geht an die Adresse auch dann, wenn die Anmeldung nicht von ihrem Inhaber stammt. Ember begrenzt diese Mails pro Adresse und pro IP-Adresse,
- die tägliche Feedback-E-Mail an den Betreiber (Abschnitt 3.10) und die Fehlermeldung zum Feedback-Versand. Beide enthalten keine Inhalte aus dem Feedback, nur Zahlen und Zeitpunkte.

Für den Versand gilt:

- Übermittelt werden die Empfängeradresse, der Betreff und der vollständige Text. Der Text enthält auch Codes und Links.
- Die E-Mails an den Betreiber landen in seinem Postfach unter nicowoellnitz.de. [[Nico: Anbieter des Postfachs nennen, mit Sitz, Rolle (Auftragsverarbeiter) und Vertrag nach Art. 28 DSGVO]]
- Resend löscht E-Mail- und Protokolldaten 30 Tage nach dem Versand. Eine einzelne E-Mail vorher löschen lässt sich nur über den Support von Resend.
- Alle gespeicherten Daten liegen in den USA, auch wenn Ember die E-Mails über die EU-Region von Resend (Irland) verschickt. Die EU-Region bestimmt nur, über welche Infrastruktur die Mails laufen und von wo sie abgehen.
- Die Unterauftragnehmer von Resend sitzen in den USA. Für den Versand nutzt Resend Amazon Web Services.
- Die Übermittlung in die USA stützt sich auf die EU-Standardvertragsklauseln (Beschluss 2021/914) im Auftragsverarbeitungsvertrag von Resend. Resend gibt nach eigenen Angaben außerdem an, am EU-US Data Privacy Framework teilzunehmen.
- Öffnungs- und Klickverfolgung sind bei Resend für alle E-Mails von Ember ausgeschaltet.
- Der Betreiber sieht jede versendete E-Mail in seinem Resend-Konto, mit Absender, Empfänger, Betreff und vollständigem Inhalt, also auch Codes und Links. Das gilt für die 30 Tage, in denen Resend die Mails speichert.

[[Nico: Inhalte und vollständige Empfänger im Resend-Dashboard weiter einsehen, oder die Speicherung abschalten? Der Text beschreibt den heutigen Stand]]

### 6.4 Push-Dienste der Browser

Wenn du Web-Push einschaltest, schickt Ember die Nachricht über den Push-Dienst deines Browsers. Je nach Browser ist das Google (Chrome und andere Chromium-Browser, auch auf Android), Mozilla (Firefox), Apple (Safari, auch für installierte Web-Apps auf dem iPhone) oder Microsoft (Edge unter Windows). Der Push-Dienst sieht die Adresse deines Browser-Abos und den Zeitpunkt der Nachricht. Den Inhalt kann er nicht lesen, weil Ember ihn verschlüsselt. Die Push-Dienste verarbeiten diese Daten als eigene Verantwortliche nach ihren eigenen Datenschutzregeln. Die Anbieter können diese Daten auch in den USA verarbeiten. Für die Übermittlung sorgen sie selbst, nach eigenen Angaben über das EU-US Data Privacy Framework oder Standardvertragsklauseln. [[Nico: DPF-Teilnahme von Google, Apple, Microsoft und Mozilla prüfen]]

### 6.5 Sonst niemand

Außer an die oben genannten Dienste (Discord, Cloudflare, Resend und die Push-Dienste der Browser) und an den Speicher für die verschlüsselten Backups (Abschnitt 7) geben wir deine Daten an niemanden weiter. Am Lagerfeuer gibst du Angaben an die Menschen weiter, die du verbindest (Abschnitt 3.13). Wir verkaufen deine Daten nicht und nutzen sie nicht für Werbung. Ember setzt keine Analyse-, Tracking- oder KI-Dienste von Drittanbietern ein. Die Nutzungszahlen in Abschnitt 6.6 sind eigene Auswertungen des Betreibers. Schriften und Skripte lädt Ember nicht von fremden Servern. Eine Weitergabe an Behörden erfolgt nur, wenn ein Gesetz es verlangt.

### 6.6 Was der Betreiber sieht

Der Betreiber hat Zugriff auf den Server und die Datenbank (Abschnitt 8). Für seine Arbeit nutzt er ein privates Dashboard. Es ist nur über sein privates Netz (Tailscale) erreichbar, und nur der Betreiber hat Zugang. Es zeigt:

- **Feedback:** Titel, Text, Bilder, Weg, Version, Zeitpunkt, ob du angemeldet warst oder anonym gesendet hast, und den Status. Der Betreiber kann private Notizen schreiben und den Status setzen. Notizen und die Position des Feedbacks auf dem Board löscht Ember zusammen mit dem Feedback (Abschnitt 3.10).
- **Nutzungszahlen:** Wie viele Menschen an einem Tag, in einer Woche oder in einem Monat aktiv waren, wie viele wiederkommen und an wie vielen Tagen Menschen aktiv sind. Dafür liest das Dashboard nur, wann ein Konto angelegt wurde und wann eine Anmeldesitzung angelegt und zuletzt genutzt wurde, mit der Konto-Kennung. Check-ins, Tiefs, Tagesnotizen, Stimmungen, Ausrutscher, „Was mir hilft“ und Am Lagerfeuer nutzt es dafür nicht, auch nicht zum Zählen. Angezeigt werden nur Summen, keine einzelnen Personen.
- **Resend-Konto:** die versendeten E-Mails mit Inhalt, 30 Tage lang (Abschnitt 6.3).

Die Nutzungszahlen stützen sich auf Art. 6 Abs. 1 lit. f DSGVO. Eine Einwilligung brauchen sie nicht. Du kannst dem widersprechen (Art. 21 DSGVO, Abschnitt 9).

## 7. Wie lange speichert Ember deine Daten?

- **Dein Konto und deine Daten** bleiben gespeichert, bis du sie löschst. Einzelne Habits, Routinen, Check-ins, Tiefs, Ausrutscher, Tagesnotizen, Habit-Notizen und Stimmungen kannst du in der App oder im Bot entfernen. Die Kontolöschung entfernt alles auf dem Server sofort, Kopien bleiben nach Abschnitt 9.
- **Habit-Notizen:** bis du sie löschst, längstens bis zum Löschen des Habits oder des Kontos. Nach einem Widerruf oder „Nein danke“ löscht Ember sie mit den anderen Daten nach Abschnitt 4 nach 30 Tagen.
- **Nach Widerruf oder „Nein danke“:** Die Daten nach Abschnitt 4 bleiben höchstens 30 Tage, nur für Export und Löschung. Dann löscht Ember sie, frühestens 3 Tage nach der Erinnerung. Konto, Einstellungen und Routinen bleiben. Der Zeitpunkt des Widerrufs bleibt als Nachweis beim Konto (Abschnitt 3.1).
- **Konten, deren E-Mail-Adresse nie bestätigt wurde,** werden nach 7 Tagen automatisch gelöscht, wenn sie noch keine Daten und keine Discord-Anmeldung haben.
- **Anmeldesitzungen:** Eine Sitzung läuft 14 Tage nach der letzten Nutzung ab. Abgelaufene Sitzungen löscht Ember einmal am Tag. Pro Konto bleiben höchstens 50 Sitzungen, bei einer neuen Anmeldung endet dann die älteste. Der Schlüssel für Check-ins aus dem Widget gilt nur für eine Sitzung und endet mit ihr.
- **Erinnerungen in Discord:** Der Eintrag bleibt, bis die Nachricht geschlossen oder ersetzt wird. Danach wird er gelöscht.
- **Knopf „Erledigt“ auf Web-Push-Erinnerungen:** Der Eintrag bleibt, bis die Erinnerung abläuft. Danach löscht Ember ihn einmal am Tag.
- **Web-Push-Abos:** bis du sie abmeldest, Benachrichtigungen ausschaltest, der Push-Dienst das Abo als ungültig meldet oder dein Konto gelöscht wird. Beim Abmelden entfernt die Web-App das Abo. Endet die Anmeldesitzung, in der du es gespeichert hast, löscht Ember das Abo ebenfalls.
- **Telefone als Erinnerungsziel:** bis die Anmeldesitzung endet, in der sich die App angemeldet hat (Abmelden oder Ablauf), oder dein Konto gelöscht wird. Pro Konto bleiben höchstens 10 Telefone; meldet sich ein elftes an, entfällt das, das sich am längsten nicht angemeldet hat.
- **Web-Push-Erinnerungen, die zugestellt wurden:** bis der Habit oder die Routine erledigt ist, spätestens am nächsten Tag um 10 Uhr.
- **Am Lagerfeuer:** Codes gelten 30 Minuten und werden danach gelöscht, ein benutzter Code sofort. Wärme-Nachrichten löscht Ember nach 30 Tagen. Verbindungen bestehen, bis du sie entfernst, Am Lagerfeuer ausschaltest oder dein Konto löschst. Fehlversuche zählt Ember für eine Stunde.
- **„Was mir hilft“:** bis du die Liste änderst, das private Tagebuch ausschaltest oder dein Konto löschst.
- **Ausrutscher:** mit dem Habit, zu dem sie gehören. Du kannst sie in der Historie auch einzeln entfernen.
- **Feedback:** 12 Monate nach dem Eingang, mit deinem Konto auch früher (anonym gesendetes Feedback nicht, es hat keinen Bezug zum Konto). **Bilder im Feedback:** 30 Tage nachdem das gemeldete Problem erledigt ist, spätestens 12 Monate nach dem Eingang. Notizen des Betreibers und die Position auf dem Feedback-Board löscht Ember zusammen mit dem Feedback.
- **Kopien der Feedback-E-Mails:** im Postfach des Betreibers und bei Resend. Neue Feedback-E-Mails enthalten keine Inhalte mehr (Abschnitt 3.10). Ältere enthielten Titel und Text. Der Betreiber löscht sie nach 12 Monaten automatisch. [[Nico: Automatische Löschung der alten Feedback-E-Mails nach 12 Monaten im Postfach einrichten und bestätigen]] Weil die E-Mails keine Konto-ID enthalten, werden sie bei einer Kontolöschung nicht einzeln gelöscht. Resend löscht sie nach 30 Tagen.
- **Resend-Protokolle und E-Mails:** 30 Tage nach dem Versand (Abschnitt 6.3).
- **Technische Protokolle:** siehe Abschnitt 3.7.
- **Backups:** Die Datenbank sichert Ember jede Nacht auf einer eigenen Festplatte desselben Servers. Wir behalten die Sicherungen der letzten 7 Tage und eine Sicherung pro Woche für die letzten 4 Wochen, ältere werden gelöscht. Vor jedem Deployment der Software gibt es zusätzlich einen Dump der Datenbank. Diese Dumps liegen auf dem Server und werden nach etwa 5 Wochen gelöscht. [[Nico: Bestätigen, ob die Dumps auch bei Proton Drive liegen und wie lange sie bleiben]] Eine Kopie der nächtlichen Sicherungen liegt Ende-zu-Ende verschlüsselt bei Proton Drive (Proton AG, Schweiz; für die Schweiz gilt ein Angemessenheitsbeschluss der EU-Kommission). Proton ist dafür Auftragsverarbeiter nach Art. 28 DSGVO. [[Nico: Bestätigen, dass dein Proton-Tarif einen Auftragsverarbeitungsvertrag (DPA) mit Proton umfasst]] Bis zur Löschung können gelöschte Daten also bis zu etwa 5 Wochen in einem Backup stehen. Backups nutzen wir nur, um den Betrieb nach einem technischen Ausfall wiederherzustellen. Müssen wir ein Backup einspielen, löschen wir die seitdem gelöschten Konten und Daten danach erneut. [[Nico: Bestätigen, dass nach einem Restore die Löschungen erneut laufen]]
- **Daten bei Discord:** nach den Regeln von Discord.
- **Ohne Konto:** auf dem Gerät, bis du sie löschst oder Browser beziehungsweise Betriebssystem sie entfernen.

## 8. Wie schützen wir deine Daten?

- **Datensparsamkeit:** Ember speichert nur die oben genannten Daten. Check-ins haben nur ein Datum, keine Uhrzeit.
- **Kein Mitlesen von Chats:** Der Bot liest keine Nachrichten mit.
- **Verschlüsselte Verbindung:** Web-App und API sind nur über HTTPS erreichbar.
- **Passwörter:** Ember speichert nur einen Hash. Nach fünf Fehlversuchen ist die Anmeldung 15 Minuten gesperrt. Zusätzlich begrenzt Ember die Anzahl der Anfragen. Die Zähler dafür stehen nur im Arbeitsspeicher.
- **Sitzungen:** Abmelden beendet nur die Sitzung auf diesem Gerät. „Überall abmelden“ in den Einstellungen beendet alle Sitzungen. Das Zurücksetzen des Passworts beendet alle Sitzungen. Das Ein- oder Ausschalten der Zwei-Faktor-Anmeldung, das Verknüpfen oder Trennen von Discord und das Zusammenführen von Konten beenden die anderen Sitzungen.
- **Schlüssel und Codes:** Der Server speichert Widget-Schlüssel und die Codes für den Knopf „Erledigt“ nur als Hash. Fireside-Codes speichert er als Hash und zusätzlich verschlüsselt, damit du sie wieder anzeigen kannst.
- **Zwei-Faktor-Anmeldung:** optional mit einer Authenticator-App und Wiederherstellungscodes.
- **Privates Tagebuch:** Ende-zu-Ende verschlüsselt (Abschnitt 3.4).
- **Privat als Standard:** Antworten auf deine Befehle sieht in der Regel nur du. Öffentlich wird nur, was du selbst postest (`/habit share`).
- **Zugriff:** Nur der Betreiber hat Zugriff auf den Server und die Datenbank. Das Dashboard zeigt nur die Angaben aus Abschnitt 6.6. Resend zeigt dem Betreiber die versendeten E-Mails (Abschnitt 6.3).
- **Löschen auf Knopfdruck:** Die Kontolöschung entfernt deine Daten aus der Datenbank auf dem Server sofort. Kopien in Backups (Abschnitt 7), auf anderen Geräten (Abschnitt 9), in technischen Protokollen mit Nutzer-IDs bis zu deren Rotation (Abschnitt 3.7), in älteren E-Mails an den Betreiber und bei Resend bleiben noch bis zu deren Löschung.

## 9. Deine Rechte

Du hast nach der DSGVO diese Rechte:

- **Auskunft** (Art. 15): welche Daten wir über dich haben.
- **Berichtigung** (Art. 16): falsche Daten korrigieren lassen.
- **Löschung** (Art. 17): deine Daten löschen lassen.
- **Einschränkung der Verarbeitung** (Art. 18).
- **Datenübertragbarkeit** (Art. 20): deine Daten in einem gängigen, maschinenlesbaren Format.
- **Widerspruch** (Art. 21) gegen Verarbeitungen auf Grundlage berechtigter Interessen (Abschnitt 5, Zeilen mit lit. f).
- **Widerruf von Einwilligungen** (Art. 7 Abs. 3), jederzeit mit Wirkung für die Zukunft (Abschnitt 4, für Am Lagerfeuer Abschnitt 3.13, für Feedback Abschnitt 3.10).

**Widerspruchsrecht.** Wenn wir Daten auf Grundlage unseres berechtigten Interesses (Art. 6 Abs. 1 lit. f DSGVO) verarbeiten, kannst du dem jederzeit aus Gründen widersprechen, die sich aus deiner besonderen Situation ergeben (Art. 21 DSGVO). Schreib an contact@nicowoellnitz.de.

Die meisten Rechte kannst du selbst ausüben:

| Was du willst | So geht es |
|---|---|
| Alle deine Daten sehen oder mitnehmen | Web-App oder App: Einstellungen, Abschnitt „Deine Daten“, Export als JSON oder CSV. Der Export enthält deine Einstellungen (mit dem Einwilligungsnachweis, Sprache, Ruhigem Modus, gesehenen Tipps und den Einstellungen für Am Lagerfeuer), die E-Mail-Adresse, die Discord-Nutzer-ID, alle Habits (mit Art und deinen Erinnerungsausnahmen), Routinen, Check-ins, Ausrutscher, Tiefs, Tagesnotizen, Habit-Notizen, Stimmungen, die Adressen deiner Push-Abos (ohne Schlüssel) mit Bezeichnung und Ein/Aus, deine Telefone (Bezeichnung, Ein/Aus, Zeitpunkt, ohne Installationskennung), den verschlüsselten Tagebuchschlüssel und die verschlüsselte Liste „Was mir hilft“. Ein verschlüsseltes Tagebuch bleibt in der Datei verschlüsselt. Die Web-App bietet außerdem einen entschlüsselten Export deines Tagebuchs an. Nicht im Export sind Verbindungen, Codes und Wärme-Nachrichten am Lagerfeuer sowie das Feedback. Dazu siehe die nächste Zeile. Discord: `/export` enthält nur Einstellungen, Habits, Check-ins, Ausrutscher, Tiefs und Routinen. Den vollständigen Export gibt es in der Web-App. Nach einem Widerruf bleibt der Export bis zur Löschung möglich. |
| Deine Einwilligung widerrufen, ohne das Konto zu löschen | Web-App oder App: Einstellungen, „Datenschutz“, „Einwilligung widerrufen“. Discord: `/privacy withdraw`. Was dann passiert, steht in Abschnitt 4. |
| Wissen, welches Feedback zu deinem Konto gespeichert ist | Feedback ist nicht im Export. Schreib an contact@nicowoellnitz.de, dann bekommst du Auskunft darüber (Art. 15 DSGVO). |
| Dein Konto löschen | Web-App oder App: Einstellungen, „Dein Konto löschen“. Mit Passwort und Eingabe von LÖSCHEN. Bei Konten ohne Passwort nur mit LÖSCHEN. Discord: `/privacy delete`, dann „Alles löschen“. Das lässt sich nicht rückgängig machen. Exportiere vorher, wenn du deine Daten behalten möchtest. Auf anderen Geräten bleiben lokale Kopien (Abschnitt 3.8), bis das Gerät den Server das nächste Mal erreicht. Dann meldet die App dich ab und löscht die Kopien. Backups behalten die Daten bis zur Rotation (Abschnitt 7), technische Protokolle mit Nutzer-IDs ebenfalls bis zu ihrer Rotation (Abschnitt 3.7). Kopien von Feedback-Mails und bei Resend bleiben nach Abschnitt 3.10 und 6.3 bestehen. Erinnerungen in Discord bleiben als Nachrichten stehen, ihre Schaltflächen führen aber ins Leere. |
| Ein Habit löschen oder umbenennen, die Häufigkeit oder die Erinnerung ändern | Discord: `/habit delete`, `/habit edit`, `/habit frequency`. Web-App: Habit öffnen, dann „Habit bearbeiten“ oder „Habit löschen“. |
| Einen Check-in korrigieren, nachtragen oder ein Tief setzen oder entfernen | Discord: `/habit history`. Web-App: Tab „Verlauf“, dann das Habit. |
| Eine Tagesnotiz, Habit-Notiz oder Stimmung ändern | Web-App oder App (Tagebuch, Habit-Notizen beim Habit). |
| Discord trennen | Web-App oder App: Einstellungen, „Discord trennen“. Das geht nur, wenn du ein Passwort und eine bestätigte E-Mail-Adresse hast, sonst könntest du dich nicht mehr anmelden. |

Für alles andere, oder wenn etwas nicht funktioniert, schreib an contact@nicowoellnitz.de. Wir antworten so schnell wie möglich, spätestens innerhalb eines Monats. Damit wir sicher sind, dass die Anfrage von dir kommt, fragen wir eventuell nach deiner Discord-Nutzer-ID oder bitten dich, dich über Discord zu bestätigen.

### Beschwerderecht

Du kannst dich bei einer Datenschutz-Aufsichtsbehörde beschweren (Art. 77 DSGVO). Für uns zuständig ist:

Landesbeauftragte für Datenschutz und Informationsfreiheit Nordrhein-Westfalen
Kavalleriestraße 2-4, 40213 Düsseldorf
www.ldi.nrw.de

Wir freuen uns aber, wenn du uns zuerst schreibst. Oft lässt sich etwas schnell klären.

## 10. Mindestalter

Ember ist für Menschen ab 16 Jahren. Wenn du den Discord-Bot nutzt, gelten außerdem die Bedingungen von Discord. Für die Einwilligung in die Verarbeitung von Gesundheitsdaten (Abschnitt 4) musst du mindestens **16** Jahre alt sein (Art. 8 Abs. 1 DSGVO). Bei der Einwilligung bestätigst du das ausdrücklich, in der Web-App und der App mit einem Häkchen, im Discord-Bot mit der Schaltfläche „Ich stimme zu, ich bin mindestens 16“. Discord lässt Nutzer schon ab 13 Jahren zu. Ember kann das Alter nicht prüfen.

## 11. Pflicht zur Bereitstellung

Du musst Ember keine Daten geben. Ohne ein Konto mit E-Mail-Adresse oder Discord-Anmeldung, ohne Habit-Namen und Check-ins kann Ember auf dem Server aber nicht arbeiten. Du kannst Ember dann vollständig auf deinem Gerät nutzen (Abschnitt 3.9). Zeitzone, Erinnerungen, Gnadentage, Tiefs, Tagesnotizen, Habit-Notizen, Stimmungen, Am Lagerfeuer und „Was mir hilft“ sind freiwillig. Die Einwilligung (Abschnitt 4) brauchst du, um Habits anzulegen und Check-ins, Tiefs, Ausrutscher, Tagesnotizen, Habit-Notizen, Stimmungen, „Was mir hilft“ oder importierte Daten zu speichern. Ohne sie oder nach einem Widerruf bleiben dein Konto und deine Einstellungen nutzbar. Deine vorhandenen Daten kannst du dann nur noch exportieren, bis Ember sie nach 30 Tagen löscht (Abschnitt 4).

## 12. Änderungen

Wenn sich Ember ändert, passen wir diese Erklärung an. Das Datum oben zeigt die aktuelle Fassung. Über wichtige Änderungen informieren wir dich in Ember. Brauchen wir für etwas Neues deine Einwilligung, fragen wir dich vorher, wie in Abschnitt 4 beschrieben.
