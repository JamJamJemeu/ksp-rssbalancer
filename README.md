# RSSBalancer

Mod do **Kerbal Space Program 1.12** do dostrajania silników w grze. Dzięki niemu rakiety z modów robionych pod skalę stockową (np. radzieckie rakiety, Tantares) działają poprawnie w większych układach planet, takich jak **Sol** czy **RSS**. Ma też wzorzec **R-7 / Sojuz** z krzyżem Korolewa.

## Funkcje

Okno otwierane klawiszem **F8**, w VAB/SPH i w locie:

- **Statek** – mnożniki **ISP ×** i **Ciąg ×** dla wszystkich silników naraz, dopasowanie całkowitej Δv do zadanej wartości.
- **Grupy** – osobne mnożniki dla każdej grupy silników oraz **masa pustych zbiorników ×** i **paliwo ×** dla zbiorników tej grupy. Grupa to silniki odpalane i odrzucane w tych samych stage'ach, więc np. boczne bloki są osobno od rdzenia, nawet przy tych samych częściach. Pokazuje czas pracy przed i po zmianie oraz przycisk „Dopasuj czas pracy”.
- **Sojuz** – automat dla układu R-7: wspólne ISP dla bocznych bloków i rdzenia, osobny ciąg dla każdego, dobrane tak, żeby czasy pracy (118 s / 286 s) i TWR na starcie (≈1,35) zgadzały się z oryginałem. Opcjonalnie też czas pracy III stopnia. Sekcja „Zbiorniki” pozwala odchudzić puste zbiorniki i dolać paliwa III stopnia, a potem od razu przelicza ISP i ciąg.
- **Misja** – kalkulator Δv potrzebnej na orbitę, do księżyca albo na planetę, liczony z parametrów planet w grze (Sol, RSS, stock). Porównanie z Δv rakiety.
- **Presety** – zapis i wczytanie ustawień wszystkich grup pod nazwą rakiety (silniki i zbiorniki). Gotowe presety z plików w `PluginData/Presets/*.cfg` wczytuje przycisk „Wczytaj presety ponownie” – bez restartu gry.

W locie, automatycznie dla rozpoznanego układu Sojuza:

- **Krzyż Korolewa** – odrzucone boczne bloki odchylają się ogonem na zewnątrz i odpadają na boki.
- **Gorące rozdzielenie** – III stopień odpala się chwilę przed wypaleniem rdzenia.
- **Oś czasu lotu** – czasy odrzutu bloków, rdzenia i końca pracy III stopnia w porównaniu z oryginałem.

Mnożniki są zapisywane per silnik w save i w pliku statku. Prefab części nie jest zmieniany. Zmiana ISP nie zmienia ciągu: przepływ paliwa jest przeliczany. Zmiana ciągu nie zmienia ISP.

## Instalacja

1. Zbuduj projekt (niżej) albo weź gotowy `RSSBalancer.dll`.
2. Skopiuj do `Kerbal Space Program/GameData/RSSBalancer/` pliki:
   - `RSSBalancer.dll`
   - `RSSBalancer.cfg` (patch ModuleManagera, który dodaje moduł do wszystkich silników)
3. Wymaga **ModuleManagera**.

Presety i ustawienia zapisują się w `GameData/RSSBalancer/PluginData/`. Gra musi mieć prawo zapisu w tym folderze.

## Budowanie

Projekt `RSSBalancer.csproj` (.NET Framework 4.7.2) korzysta z bibliotek KSP z `KSP_x64_Data/Managed`. Ścieżkę do gry ustawiasz we właściwości `KSPDir` w pliku `.csproj`. Po zbudowaniu DLL i patch kopiują się same do `GameData/RSSBalancer`.

## Pliki

| Plik | Co robi |
|---|---|
| `ModuleEngineBalancer.cs` | moduł silnika: mnożniki ISP i ciągu, zapis w save |
| `ModuleTankBalancer.cs` | moduł zbiornika: masa pustego ×, paliwo × |
| `EngineBalancer.cs` | okno F8 z zakładkami |
| `EngineGroups.cs` | grupowanie silników po odpaleniu i odrzucie |
| `SoyuzTemplate.cs` | automat R-7 / Sojuz |
| `FlightFeatures.cs` | krzyż Korolewa, gorące rozdzielenie, oś czasu lotu |
| `DeltaVPlanner.cs` | kalkulator Δv misji |
| `StockDeltaV.cs` | odczyt stockowych statystyk Δv / TWR / czasu pracy |
| `PresetStore.cs`, `BalancerSettings.cs` | presety i ustawienia |
| `GameData/RSSBalancer/RSSBalancer.cfg` | patch ModuleManagera |
