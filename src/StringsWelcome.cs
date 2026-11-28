using System;

namespace AIMeter
{
    internal static partial class L
    {
        /// <summary>First-launch consent and sample data strings, registered like the history strings.</summary>
        private static void RegisterWelcomeStrings()
        {
            string[] languages = { "it", "es", "fr", "de", "pt" };
            string[][] rows =
            {
                new[] { "Welcome to AI Meter", "Benvenuto in AI Meter", "Te damos la bienvenida a AI Meter", "Bienvenue dans AI Meter", "Willkommen bei AI Meter", "Bem-vindo ao AI Meter" },
                new[] { "AI Meter reads the sessions and limits that AI apps save on this PC. Each choice can be changed later in Settings.",
                        "AI Meter legge le sessioni e i limiti che le app di IA salvano su questo PC. Ogni scelta si può cambiare in seguito nelle Impostazioni.",
                        "AI Meter lee las sesiones y los límites que las apps de IA guardan en este PC. Cada opción se puede cambiar más tarde en Configuración.",
                        "AI Meter lit les sessions et les limites que les apps d’IA enregistrent sur ce PC. Chaque choix peut être modifié plus tard dans les Paramètres.",
                        "AI Meter liest die Sitzungen und Limits, die KI-Apps auf diesem PC speichern. Jede Auswahl lässt sich später in den Einstellungen ändern.",
                        "O AI Meter lê as sessões e os limites que os apps de IA salvam neste PC. Cada opção pode ser alterada depois nas Configurações." },
                new[] { "Show in the taskbar", "Mostra nella barra delle applicazioni", "Mostrar en la barra de tareas", "Afficher dans la barre des tâches", "In der Taskleiste anzeigen", "Mostrar na barra de tarefas" },
                new[] { "When off, AI Meter shows a widget on the desktop instead",
                        "Se disattivato, AI Meter mostra un widget sul desktop",
                        "Si se desactiva, AI Meter muestra un widget en el escritorio",
                        "Si désactivé, AI Meter affiche un widget sur le bureau",
                        "Wenn deaktiviert, zeigt AI Meter ein Widget auf dem Desktop",
                        "Se desativado, o AI Meter mostra um widget na área de trabalho" },
                new[] { "Privacy policy", "Informativa sulla privacy", "Política de privacidad", "Politique de confidentialité", "Datenschutzerklärung", "Política de privacidade" },
                new[] { "Preview with sample data", "Prova con dati di esempio", "Probar con datos de ejemplo", "Essayer avec des données d’exemple", "Mit Beispieldaten ausprobieren", "Experimentar com dados de exemplo" },
                new[] { "Start", "Inizia", "Empezar", "Commencer", "Starten", "Começar" },
                new[] { "Sample data", "Dati di esempio", "Datos de ejemplo", "Données d’exemple", "Beispieldaten", "Dados de exemplo" },
                new[] { "Shows invented figures to try AI Meter without signing in. Real data returns at the next start.",
                        "Mostra valori inventati per provare AI Meter senza accedere ai servizi. I dati reali tornano al prossimo avvio.",
                        "Muestra cifras inventadas para probar AI Meter sin iniciar sesión. Los datos reales vuelven en el próximo inicio.",
                        "Affiche des valeurs inventées pour essayer AI Meter sans connexion aux services. Les vraies données reviennent au prochain démarrage.",
                        "Zeigt erfundene Werte, um AI Meter ohne Anmeldung auszuprobieren. Beim nächsten Start kehren die echten Daten zurück.",
                        "Mostra valores inventados para experimentar o AI Meter sem entrar nos serviços. Os dados reais voltam na próxima inicialização." },
                new[] { "Leave sample data", "Esci dai dati di esempio", "Salir de los datos de ejemplo", "Quitter les données d’exemple", "Beispieldaten beenden", "Sair dos dados de exemplo" },
                new[] { "Showing sample data", "Dati di esempio attivi", "Mostrando datos de ejemplo", "Données d’exemple affichées", "Beispieldaten werden angezeigt", "A mostrar dados de exemplo" },
                new[] { "The figures are invented. Choose Leave sample data from the notification area menu, or turn it off in Settings → About.",
                        "I valori sono inventati. Scegli Esci dai dati di esempio dal menu dell’area di notifica, oppure disattivali in Impostazioni → Informazioni.",
                        "Las cifras son inventadas. Elige Salir de los datos de ejemplo en el menú del área de notificación o desactívalos en Configuración → Información.",
                        "Les valeurs sont inventées. Choisissez Quitter les données d’exemple dans le menu de la zone de notification, ou désactivez-les dans Paramètres → À propos.",
                        "Die Werte sind erfunden. Wählen Sie im Menü des Infobereichs Beispieldaten beenden oder schalten Sie sie unter Einstellungen → Info aus.",
                        "Os valores são inventados. Escolha Sair dos dados de exemplo no menu da área de notificação, ou desative-os em Configurações → Sobre." },
            };
            foreach (string[] row in rows)
                for (int i = 0; i < languages.Length; i++) Register(languages[i], row[0], row[i + 1]);
        }
    }
}
