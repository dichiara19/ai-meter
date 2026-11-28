using System;

namespace AIMeter
{
    internal static partial class L
    {
        /// <summary>Update check strings in Settings → About and its notification, registered like the welcome strings.</summary>
        private static void RegisterUpdateStrings()
        {
            string[] languages = { "it", "es", "fr", "de", "pt" };
            string[][] rows =
            {
                new[] { "Updates", "Aggiornamenti", "Actualizaciones", "Mises à jour", "Updates", "Atualizações" },
                new[] { "Check for updates", "Controlla gli aggiornamenti", "Buscar actualizaciones", "Rechercher des mises à jour", "Nach Updates suchen", "Procurar atualizações" },
                new[] { "Looks for new versions on GitHub once a day",
                        "Cerca nuove versioni su GitHub una volta al giorno",
                        "Busca nuevas versiones en GitHub una vez al día",
                        "Recherche les nouvelles versions sur GitHub une fois par jour",
                        "Sucht einmal täglich auf GitHub nach neuen Versionen",
                        "Procura novas versões no GitHub uma vez por dia" },
                new[] { "Include pre-releases", "Includi le versioni di anteprima", "Incluir versiones preliminares", "Inclure les préversions", "Vorabversionen einbeziehen", "Incluir versões de pré-lançamento" },
                new[] { "Beta and release candidate versions, which can be less stable",
                        "Versioni beta e release candidate, che possono essere meno stabili",
                        "Versiones beta y candidatas, que pueden ser menos estables",
                        "Versions bêta et candidates, qui peuvent être moins stables",
                        "Beta- und Release-Candidate-Versionen, die weniger stabil sein können",
                        "Versões beta e candidatas, que podem ser menos estáveis" },
                new[] { "AI Meter {0} is available", "AI Meter {0} è disponibile", "AI Meter {0} está disponible", "AI Meter {0} est disponible", "AI Meter {0} ist verfügbar", "O AI Meter {0} está disponível" },
                new[] { "Installed version: {0}", "Versione installata: {0}", "Versión instalada: {0}", "Version installée : {0}", "Installierte Version: {0}", "Versão instalada: {0}" },
                new[] { "Release page", "Pagina della versione", "Página de la versión", "Page de la version", "Versionsseite", "Página da versão" },
                new[] { "Shows the updates waiting in the Microsoft Store",
                        "Segnala gli aggiornamenti in attesa nel Microsoft Store",
                        "Muestra las actualizaciones pendientes en Microsoft Store",
                        "Signale les mises à jour en attente dans le Microsoft Store",
                        "Zeigt Updates an, die im Microsoft Store bereitstehen",
                        "Mostra as atualizações pendentes na Microsoft Store" },
                new[] { "An update is waiting in the Microsoft Store", "Un aggiornamento è in attesa nel Microsoft Store", "Hay una actualización pendiente en Microsoft Store", "Une mise à jour attend dans le Microsoft Store", "Im Microsoft Store wartet ein Update", "Há uma atualização pendente na Microsoft Store" },
                new[] { "Open the Store", "Apri lo Store", "Abrir la Store", "Ouvrir le Store", "Store öffnen", "Abrir a Store" },
            };
            foreach (string[] row in rows)
                for (int i = 0; i < languages.Length; i++) Register(languages[i], row[0], row[i + 1]);
        }
    }
}
