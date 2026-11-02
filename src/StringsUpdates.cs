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
                new[] { "Once a day, ask GitHub whether a newer stable version exists and show a notice. Nothing is downloaded or installed, and the request includes no account or identifier.",
                        "Una volta al giorno chiede a GitHub se esiste una versione stabile più recente e mostra un avviso. Non scarica né installa nulla, e la richiesta non include account né identificativi.",
                        "Una vez al día pregunta a GitHub si hay una versión estable más reciente y muestra un aviso. No se descarga ni se instala nada, y la solicitud no incluye cuentas ni identificadores.",
                        "Une fois par jour, demande à GitHub si une version stable plus récente existe et affiche un avis. Rien n’est téléchargé ni installé, et la requête ne contient ni compte ni identifiant.",
                        "Fragt einmal täglich bei GitHub nach, ob es eine neuere stabile Version gibt, und zeigt einen Hinweis. Es wird nichts heruntergeladen oder installiert, und die Anfrage enthält weder Konto noch Kennung.",
                        "Uma vez por dia, pergunta ao GitHub se existe uma versão estável mais recente e mostra um aviso. Nada é transferido nem instalado, e o pedido não inclui contas nem identificadores." },
                new[] { "Include pre-releases", "Includi le versioni di anteprima", "Incluir versiones preliminares", "Inclure les préversions", "Vorabversionen einbeziehen", "Incluir versões de pré-lançamento" },
                new[] { "Also tell me about beta and release candidate versions, which can be less stable.",
                        "Segnala anche le versioni beta e release candidate, che possono essere meno stabili.",
                        "Avisar también de las versiones beta y candidatas, que pueden ser menos estables.",
                        "Signaler aussi les versions bêta et candidates, qui peuvent être moins stables.",
                        "Auch über Beta- und Release-Candidate-Versionen informieren, die weniger stabil sein können.",
                        "Avisar também das versões beta e candidatas, que podem ser menos estáveis." },
                new[] { "AI Meter {0} is available", "AI Meter {0} è disponibile", "AI Meter {0} está disponible", "AI Meter {0} est disponible", "AI Meter {0} ist verfügbar", "O AI Meter {0} está disponível" },
                new[] { "You are using {0}.", "Stai usando la {0}.", "Estás usando la {0}.", "Vous utilisez la {0}.", "Sie verwenden {0}.", "Está a utilizar a {0}." },
                new[] { "Release page", "Pagina della versione", "Página de la versión", "Page de la version", "Versionsseite", "Página da versão" },
                new[] { "You are using {0}. Click to open the release page: nothing is downloaded automatically.",
                        "Stai usando la {0}. Fai clic per aprire la pagina della versione: non viene scaricato nulla automaticamente.",
                        "Estás usando la {0}. Haz clic para abrir la página de la versión: no se descarga nada automáticamente.",
                        "Vous utilisez la {0}. Cliquez pour ouvrir la page de la version : rien n’est téléchargé automatiquement.",
                        "Sie verwenden {0}. Klicken Sie, um die Versionsseite zu öffnen: Es wird nichts automatisch heruntergeladen.",
                        "Está a utilizar a {0}. Clique para abrir a página da versão: nada é transferido automaticamente." },
                new[] { "The Microsoft Store installs updates by itself. Once a day AI Meter also asks the Store whether one is waiting, to show it here; it never contacts GitHub.",
                        "Il Microsoft Store installa gli aggiornamenti da solo. Una volta al giorno AI Meter chiede anche allo Store se ce n’è uno in attesa, per mostrarlo qui; non contatta mai GitHub.",
                        "Microsoft Store instala las actualizaciones por sí solo. Una vez al día AI Meter también pregunta a la Store si hay alguna pendiente, para mostrarla aquí; nunca contacta con GitHub.",
                        "Le Microsoft Store installe les mises à jour tout seul. Une fois par jour, AI Meter demande aussi au Store si une mise à jour attend, pour l’afficher ici ; il ne contacte jamais GitHub.",
                        "Der Microsoft Store installiert Updates selbst. Einmal täglich fragt AI Meter zusätzlich den Store, ob ein Update bereitsteht, um es hier anzuzeigen; GitHub wird nie kontaktiert.",
                        "A Microsoft Store instala as atualizações sozinha. Uma vez por dia, o AI Meter pergunta também à Store se há alguma pendente, para a mostrar aqui; nunca contacta o GitHub." },
                new[] { "An update is waiting in the Microsoft Store", "Un aggiornamento è in attesa nel Microsoft Store", "Hay una actualización pendiente en Microsoft Store", "Une mise à jour attend dans le Microsoft Store", "Im Microsoft Store wartet ein Update", "Há uma atualização pendente na Microsoft Store" },
                new[] { "Open the Store", "Apri lo Store", "Abrir la Store", "Ouvrir le Store", "Store öffnen", "Abrir a Store" },
                new[] { "The update check below asks the Microsoft Store whether an update is waiting.",
                        "Il controllo degli aggiornamenti qui sotto chiede al Microsoft Store se un aggiornamento è in attesa.",
                        "La búsqueda de actualizaciones de abajo pregunta a Microsoft Store si hay una actualización pendiente.",
                        "La recherche de mises à jour ci-dessous demande au Microsoft Store si une mise à jour attend.",
                        "Die Update-Suche unten fragt den Microsoft Store, ob ein Update bereitsteht.",
                        "A procura de atualizações abaixo pergunta à Microsoft Store se há uma atualização pendente." },
                new[] { "The update check below also asks GitHub for the latest version.",
                        "Il controllo degli aggiornamenti qui sotto chiede anche a GitHub qual è l’ultima versione.",
                        "La búsqueda de actualizaciones de abajo también pregunta a GitHub cuál es la última versión.",
                        "La recherche de mises à jour ci-dessous demande aussi à GitHub la dernière version.",
                        "Die Update-Suche unten fragt außerdem GitHub nach der neuesten Version.",
                        "A procura de atualizações abaixo pergunta também ao GitHub qual é a versão mais recente." },
            };
            foreach (string[] row in rows)
                for (int i = 0; i < languages.Length; i++) Register(languages[i], row[0], row[i + 1]);
        }
    }
}
