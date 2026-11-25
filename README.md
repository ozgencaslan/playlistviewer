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
5. Eine Stream-URL durch Doppelklick oder über das Kontextmenü kopieren. Über **M3U speichern** die Originaldatei lokal ablegen.

Die zuletzt erfolgreich geladene URL wird unter `%LOCALAPPDATA%\PlaylistViewer\last-playlist-url.txt` gespeichert. Falls die URL Zugangsdaten oder einen privaten Token enthält, sollte sie nicht öffentlich geteilt werden.

## Build

Voraussetzung für die Entwicklung: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build .\PlaylistViewer.csproj -c Release
dotnet publish .\PlaylistViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

Die veröffentlichte EXE ist selbstständig ausführbar. Build-Ausgaben, lokale IDE-Dateien und `publish` werden durch `.gitignore` nicht in normale Git-Commits aufgenommen.

## Versionshistorie

Änderungen pro Version stehen in [CHANGELOG.md](CHANGELOG.md). Die Versionsnummer wird in `PlaylistViewer.csproj` gepflegt.
