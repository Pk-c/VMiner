# VMiner

VMiner lit le japonais directement à l'écran sous Windows : maintiens la touche de capture,
déplace la souris jusqu'à l'autre coin de la zone, puis relâche la touche. Aucun clic n'est
nécessaire.

La version 2 est une application native **C# / .NET 10 / WPF**. Elle est distribuable comme
un dossier portable et fonctionne entièrement en local : capture, OCR, furigana et traduction
japonais → anglais.

## Utilisation

1. Lance `VMiner.exe`.
2. Place le curseur sur un coin du dialogue.
3. Maintiens la touche configurée (Shift gauche par défaut), déplace la souris, puis relâche-la.
4. VMiner affiche le texte japonais, ses furigana, sa lecture en kana et sa traduction anglaise.
5. Survole puis clique un mot pour générer sa définition anglaise et l'ajouter à ta collection.

Échap ou un clic droit annule une sélection. Le bouton **Minimize** garde VMiner visible dans
la barre des tâches. Le bouton de fermeture Windows et **Exit** ferment réellement l'application.

## Traduction locale

Le moteur utilise un modèle TranslateGemma 4B au format GGUF. Le modèle local est placé ici
mais reste exclu du contrôle de version à cause de sa taille et de sa licence :

```text
models\translategemma-4b-it-Q4_K_M.gguf
```

Sans ce fichier, l'OCR et les furigana fonctionnent normalement et l'interface indique que la
traduction est indisponible. Le modèle est chargé par LLamaSharp avec son backend Vulkan ;
aucun texte n'est envoyé sur Internet.

La langue est volontairement fixée à l'anglais pour garder l'interface et le moteur simples.

## Collection de vocabulaire

Au premier lancement, VMiner demande où créer le fichier JSON de la collection. Son emplacement
peut ensuite être modifié depuis la fenêtre principale. Chaque entrée contient la forme dictionnaire
japonaise, sa lecture, une définition anglaise et autant de couples phrase japonaise / traduction
anglaise que nécessaire. Les mots déjà présents sont fusionnés et reçoivent le nouvel exemple sans
dupliquer les phrases existantes.

L'onglet **Collection** permet de rechercher dans tous les champs, modifier un mot et ses exemples,
ou supprimer une entrée complète. Un double-clic ouvre également l'éditeur.

## Compiler

Prérequis : SDK .NET 10 sous Windows 10 ou 11.

```powershell
dotnet build src\VMiner.App\VMiner.App.csproj -c Debug
```

Test local de l'OCR, des furigana et de la traduction :

```powershell
VMiner.exe --self-test .\self-test.txt
Get-Content .\self-test.txt
```

## Reconstruire l'application portable

```powershell
.\build-portable.ps1
```

Le script replace directement `VMiner.exe`, `IpaDic\` et `runtimes\` à la racine, tout en
préservant le modèle déjà présent dans `models\`. L'ensemble est autonome : .NET n'a pas
besoin d'être installé sur la machine cible.

Pour distribuer VMiner, copie `VMiner.exe` avec les trois dossiers `IpaDic`, `models` et
`runtimes`. Le dossier `src` et le script de build ne sont nécessaires que pour développer.

## Architecture

| Élément | Implémentation locale |
| --- | --- |
| Interface | WPF sur .NET 10 |
| Capture | hooks Win32 + capture GDI en mémoire |
| OCR | moteur japonais intégré à Windows, image agrandie ×4 |
| Furigana | Kawazu / MeCab |
| Segmentation | MeCab IPA, avec forme dictionnaire et lecture |
| Traduction | TranslateGemma GGUF via LLamaSharp |
| Collection | fichier JSON choisi par l'utilisateur |
| Configuration | `config.json` à côté de l'exécutable |

Le dossier principal du port est `src\VMiner.App`. La capture et l'analyse restent en mémoire ;
aucune image temporaire n'est écrite sur le disque.
