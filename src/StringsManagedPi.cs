using System;

namespace AIMeter
{
    internal static partial class L
    {
        /// <summary>
        /// The managed Pi's strings (ADR 0004, ADR 0005) in Settings › MCP › Delegation agent and in its installer's messages,
        /// registered like the update strings.
        /// </summary>
        private static void RegisterManagedPiStrings()
        {
            string[] languages = { "it", "es", "fr", "de", "pt" };
            foreach (string[] row in ManagedPiStrings)
                for (int i = 0; i < languages.Length; i++) Register(languages[i], row[0], row[i + 1]);
        }

        /// <summary>English, then Italian, Spanish, French, German and Portuguese.</summary>
        internal static readonly string[][] ManagedPiStrings =
        {
            new[] { "Looking for your coding agents…", "Cerco i tuoi agenti di programmazione…", "Buscando tus agentes de programación…", "Recherche de vos agents de programmation…",
                    "Ihre Coding-Agenten werden gesucht…", "A procurar os seus agentes de programação…" },
            new[] { "{0} on Windows", "{0} su Windows", "{0} en Windows", "{0} sous Windows", "{0} unter Windows", "{0} no Windows" },
            new[] { "{0} in WSL ({1})", "{0} dentro WSL ({1})", "{0} en WSL ({1})", "{0} dans WSL ({1})", "{0} unter WSL ({1})", "{0} no WSL ({1})" },
            new[] { "Pi inside WSL ({0})", "Pi dentro WSL ({0})", "Pi dentro de WSL ({0})", "Pi dans WSL ({0})", "Pi in WSL ({0})", "Pi dentro do WSL ({0})" },
            new[] { "Pi inside WSL", "Pi dentro WSL", "Pi dentro de WSL", "Pi dans WSL", "Pi in WSL", "Pi dentro do WSL" },
            new[] { "Pi on Windows", "Pi su Windows", "Pi en Windows", "Pi sous Windows", "Pi unter Windows", "Pi no Windows" },
            new[] { "WSL is not running. Start it to install Pi there; AI Meter does not start it.",
                    "WSL non è in esecuzione. Avvialo per installarvi Pi; AI Meter non lo avvia.",
                    "WSL no está en ejecución. Inícialo para instalar Pi allí; AI Meter no lo inicia.",
                    "WSL n’est pas en cours d’exécution. Démarrez-le pour y installer Pi ; AI Meter ne le démarre pas.",
                    "WSL läuft nicht. Starten Sie es, um Pi dort zu installieren; AI Meter startet es nicht.",
                    "O WSL não está em execução. Inicie-o para lá instalar o Pi; o AI Meter não o inicia." },
            new[] { "WSL is not running. Start it to remove Pi from there; AI Meter does not start it.",
                    "WSL non è in esecuzione. Avvialo per rimuovere Pi da lì; AI Meter non lo avvia.",
                    "WSL no está en ejecución. Inícialo para quitar Pi de allí; AI Meter no lo inicia.",
                    "WSL n’est pas en cours d’exécution. Démarrez-le pour en supprimer Pi ; AI Meter ne le démarre pas.",
                    "WSL läuft nicht. Starten Sie es, um Pi dort zu entfernen; AI Meter startet es nicht.",
                    "O WSL não está em execução. Inicie-o para remover de lá o Pi; o AI Meter não o inicia." },
            new[] { "Pi's settings folder inside WSL could not be made private. Nothing was installed.",
                    "Non è stato possibile rendere privata la cartella delle impostazioni di Pi dentro WSL. Non è stato installato nulla.",
                    "No se pudo hacer privada la carpeta de configuración de Pi dentro de WSL. No se ha instalado nada.",
                    "Le dossier de réglages de Pi dans WSL n’a pas pu être rendu privé. Rien n’a été installé.",
                    "Pis Einstellungsordner in WSL konnte nicht privat gemacht werden. Es wurde nichts installiert.",
                    "Não foi possível tornar privada a pasta de definições do Pi dentro do WSL. Nada foi instalado." },
            new[] { "Pi could not be unpacked inside WSL. Nothing was installed.",
                    "Non è stato possibile estrarre Pi dentro WSL. Non è stato installato nulla.",
                    "No se pudo descomprimir Pi dentro de WSL. No se ha instalado nada.",
                    "Pi n’a pas pu être décompressé dans WSL. Rien n’a été installé.",
                    "Pi konnte in WSL nicht entpackt werden. Es wurde nichts installiert.",
                    "Não foi possível extrair o Pi dentro do WSL. Nada foi instalado." },
            new[] { "Download", "Scarica", "Descargar", "Télécharger", "Herunterladen", "Transferir" },
            new[] { "Downloading {0} of {1} MB…", "Scarico {0} di {1} MB…", "Descargando {0} de {1} MB…", "Téléchargement de {0} sur {1} Mo…", "{0} von {1} MB werden heruntergeladen…", "A transferir {0} de {1} MB…" },
            new[] { "Get Git for Windows", "Scarica Git per Windows", "Obtener Git para Windows", "Obtenir Git pour Windows", "Git für Windows holen", "Obter o Git para Windows" },
            new[] { "Pi's key file could not be written. Check that no delegated task is running, then try again.",
                    "Non è stato possibile scrivere il file di chiavi di Pi. Controlla che nessuna attività delegata sia in corso, poi riprova.",
                    "No se pudo escribir el archivo de claves de Pi. Comprueba que no haya ninguna tarea delegada en curso y vuelve a intentarlo.",
                    "Le fichier de clés de Pi n’a pas pu être écrit. Vérifiez qu’aucune tâche déléguée n’est en cours, puis réessayez.",
                    "Pis Schlüsseldatei konnte nicht geschrieben werden. Prüfen Sie, dass keine delegierte Aufgabe läuft, und versuchen Sie es erneut.",
                    "Não foi possível escrever o ficheiro de chaves do Pi. Verifique que não há nenhuma tarefa delegada em curso e tente de novo." },
            new[] { "This choice could not be saved, so it will not last past a restart. Check AI Meter's settings file, then change it again.",
                    "Non è stato possibile salvare questa scelta, quindi non resterà dopo un riavvio. Controlla il file delle impostazioni di AI Meter, poi cambiala di nuovo.",
                    "No se pudo guardar esta elección, así que no se conservará tras reiniciar. Comprueba el archivo de configuración de AI Meter y cámbiala de nuevo.",
                    "Ce choix n’a pas pu être enregistré et ne sera donc pas conservé après un redémarrage. Vérifiez le fichier de réglages d’AI Meter, puis modifiez-le de nouveau.",
                    "Diese Auswahl konnte nicht gespeichert werden und bleibt daher nach einem Neustart nicht erhalten. Prüfen Sie die Einstellungsdatei von AI Meter und ändern Sie sie dann erneut.",
                    "Não foi possível guardar esta escolha, pelo que não se manterá após um reinício. Verifique o ficheiro de definições do AI Meter e altere-a de novo." },
            new[] { "The downloaded file is not the expected Pi release. It was deleted and nothing was installed.",
                    "Il file scaricato non è la versione di Pi attesa. È stato cancellato e non è stato installato nulla.",
                    "El archivo descargado no es la versión de Pi esperada. Se ha eliminado y no se ha instalado nada.",
                    "Le fichier téléchargé n’est pas la version de Pi attendue. Il a été supprimé et rien n’a été installé.",
                    "Die heruntergeladene Datei ist nicht die erwartete Pi-Version. Sie wurde gelöscht, und nichts wurde installiert.",
                    "O ficheiro transferido não é a versão do Pi esperada. Foi eliminado e nada foi instalado." },
            new[] { "The downloaded archive does not contain Pi's program. Nothing was installed.",
                    "L’archivio scaricato non contiene il programma di Pi. Non è stato installato nulla.",
                    "El archivo descargado no contiene el programa de Pi. No se ha instalado nada.",
                    "L’archive téléchargée ne contient pas le programme de Pi. Rien n’a été installé.",
                    "Das heruntergeladene Archiv enthält Pis Programm nicht. Es wurde nichts installiert.",
                    "O arquivo transferido não contém o programa do Pi. Nada foi instalado." },
            new[] { "The downloaded archive could not be read. Nothing was installed.",
                    "Non è stato possibile leggere l’archivio scaricato. Non è stato installato nulla.",
                    "No se pudo leer el archivo descargado. No se ha instalado nada.",
                    "L’archive téléchargée n’a pas pu être lue. Rien n’a été installé.",
                    "Das heruntergeladene Archiv konnte nicht gelesen werden. Es wurde nichts installiert.",
                    "Não foi possível ler o arquivo transferido. Nada foi instalado." },
            new[] { "The download from GitHub failed. Check the connection and try again.",
                    "Lo scaricamento da GitHub non è riuscito. Controlla la connessione e riprova.",
                    "La descarga desde GitHub ha fallado. Comprueba la conexión y vuelve a intentarlo.",
                    "Le téléchargement depuis GitHub a échoué. Vérifiez la connexion et réessayez.",
                    "Der Download von GitHub ist fehlgeschlagen. Prüfen Sie die Verbindung und versuchen Sie es erneut.",
                    "A transferência a partir do GitHub falhou. Verifique a ligação e tente de novo." },
            new[] { "Pi could not be removed: one of its files is in use. Wait for delegated tasks to end, then try again.",
                    "Non è stato possibile rimuovere Pi: uno dei suoi file è in uso. Aspetta la fine dele attività delegate, poi riprova.",
                    "No se pudo quitar Pi: uno de sus archivos está en uso. Espera a que terminen las tareas delegadas y vuelve a intentarlo.",
                    "Pi n’a pas pu être supprimé : l’un de ses fichiers est utilisé. Attendez la fin des tâches déléguées, puis réessayez.",
                    "Pi konnte nicht entfernt werden: Eine seiner Dateien wird verwendet. Warten Sie, bis delegierte Aufgaben beendet sind, und versuchen Sie es erneut.",
                    "Não foi possível remover o Pi: um dos seus ficheiros está em uso. Aguarde o fim das tarefas delegadas e tente de novo." },
        };
    }
}
