# Playlist Viewer

Eine Windows-Desktop-App zum Herunterladen, Durchsuchen und Organisieren von M3U/M3U8-Playlists.

- **Aktuelle Version:** 1.0.5
- **Plattform:** Windows x64
- **Oberfläche:** Deutsch

## Funktionen

- M3U- oder M3U8-Dateien von einer HTTP- oder HTTPS-URL laden.
- Die zuletzt erfolgreich geladene URL beim nächsten Start wiederherstellen.
- Einträge nach Name, Titel, Gruppe, TVG-ID oder Stream-URL durchsuchen.
- Spaltenwerte filtern, sortieren und gruppieren.
- Beim Auswählen einer Gruppe in der Gruppen-Combobox werden Suche und andere Filter zurückgesetzt, damit alle Einträge der Gruppe angezeigt werden.
- Ein Klick auf einen Playlist-Eintrag öffnet dessen Stream-URL in VLC. VLC muss installiert sein (Standard-Installationspfad oder über `vlc.exe` im `PATH` erreichbar).
- Große Ergebnislisten mit bis zu 2.000 Zeilen pro Seite anzeigen.
- Episoden wie `Peacemaker s01 e01` unter einer Serienzusammenfassung gruppieren. Gruppen sind anfangs eingeklappt; die Zusammenfassung klappt die Folgen ein oder aus.
- Stream-URLs kopieren und die heruntergeladene Original-Playlist lokal speichern.
- Playlist-Größe, Eintrags- und Gruppenzahl sowie Dauer und Zeitpunkt der letzten Aktion in der Statusleiste anzeigen.

## Anwendung starten

Die eigenständige Windows-x64-Version liegt unter `publish\PlaylistViewer.exe`. Sie benötigt keine separate .NET-Installation.

1. Eine Playlist-URL eingeben und **Herunterladen** wählen.
2. Über das Suchfeld, den Gruppenfilter oder die Filtermenüs der Tabellenköpfe die Einträge einschränken.
3. Für Serien die Checkbox **Serien gruppieren** aktivieren. Auf eine Serienzusammenfassung klicken, um ihre Episoden ein- oder auszublenden.
4. Bei mehreren Ergebnisseiten über die Pfeile in der Statusleiste navigieren.
5. Einen Eintrag anklicken, um den Stream in VLC abzuspielen. Die Stream-URL lässt sich über das Kontextmenü oder **Strg+C** kopieren. Über **M3U speichern** die Originaldatei lokal ablegen.

Die zuletzt erfolgreich geladene URL wird unter `%LOCALAPPDATA%\PlaylistViewer\last-playlist-url.txt` gespeichert. Falls die URL Zugangsdaten oder einen privaten Token enthält, sollte sie nicht öffentlich geteilt werden.

## Build

Voraussetzung für die Entwicklung: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build .\PlaylistViewer.csproj -c Release
dotnet publish .\PlaylistViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

Die veröffentlichte EXE ist selbstständig ausführbar. Build-Ausgaben, lokale IDE-Dateien und `publish` werden durch `.gitignore` nicht in normale Git-Commits aufgenommen.

## Android-App

Das Android-Projekt befindet sich unter `android` und unterstützt Suche, Gruppen- und Serienfilter, Spaltensortierung, Wertfilter, URL-Kopieren, externe Stream-Wiedergabe und das Teilen/Speichern der M3U-Datei. Die Oberfläche folgt der Gerätesprache (Deutsch oder Englisch). Das Projekt benötigt das .NET-10-Android-Workload, Android SDK 36 und ein Java 17-JDK. Das Workload lässt sich mit `dotnet workload install maui-android` installieren.

```powershell
dotnet build .\android\PlaylistViewer.Android.csproj -c Release
dotnet publish .\android\PlaylistViewer.Android.csproj -c Release -p:AndroidPackageFormats=apk
```

Debug-Builds unterstützen HTTP für lokale Testserver. Release-Builds und die Google-Play-Version verlangen HTTPS für Playlists und Streams. HTTPS ist auch für private Server empfohlen, da HTTP Zugangsdaten und Tokens unverschlüsselt überträgt.

Für den lokalen HTTPS-Server `192.168.0.47` akzeptiert die Android-App ein sonst ungültiges Zertifikat nur, wenn dessen SHA-256-Fingerabdruck exakt dem in `MainPage.cs` hinterlegten Pin entspricht. Bei einem Zertifikatswechsel muss dieser Pin aktualisiert werden; die Zertifikatsprüfung für andere Hosts bleibt unverändert.

Die zuletzt eingegebene Playlist-URL wird lokal gespeichert, aber von Android-Cloud-Backups und Geräteübertragungen ausgeschlossen. Die App enthält keine Werbe- oder Analyse-SDKs. Beim Laden wird die URL an den vom Nutzer gewählten Playlist-Server gesendet; beim Abspielen wird die Stream-URL an eine installierte kompatible App übergeben. **M3U teilen/speichern** öffnet die Android-Teilen-Oberfläche.

### Google Play Release

Neue Apps und Updates müssen laut [Google Play Ziel-API-Vorgaben](https://support.google.com/googleplay/android-developer/answer/11926878) derzeit Android 16 / API 36 oder höher targeten. Das Projekt ist auf API 36 festgelegt und veröffentlicht für Google Play ein Android App Bundle (AAB):

```powershell
dotnet publish .\android\PlaylistViewer.Android.csproj -c Release -p:PublishProfile=GooglePlay
```

Das Profil verlangt ein eigenes Upload-Keystore. Vor dem Publish müssen diese Umgebungsvariablen gesetzt sein: `PLAY_STORE_KEYSTORE_PATH`, `PLAY_STORE_KEY_ALIAS`, `PLAY_STORE_STORE_PASSWORD` und `PLAY_STORE_KEY_PASSWORD`. Keystore und Passwörter nicht ins Repository einchecken. Die Paket-ID `com.ozgencaslan.playlistviewer` ist nach dem ersten Store-Upload unveränderlich; `ApplicationVersion` muss für jedes weitere Update erhöht werden.

Vor der Einreichung müssen im Play Console-Konto außerdem ein öffentlich erreichbarer Datenschutztext samt URL, Entwicklerkontakt, Store-Einträge und Grafiken, Inhaltsfreigabe, Zielgruppe, Data-Safety-Angaben sowie die Test- und Review-Schritte vervollständigt werden. Diese konto- und inhaberspezifischen Angaben können nicht aus dem Quellcode erzeugt oder hier veröffentlicht werden.

## Versionshistorie

Änderungen pro Version stehen in [CHANGELOG.md](CHANGELOG.md). Die Versionsnummer wird in `PlaylistViewer.csproj` gepflegt.
