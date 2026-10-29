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
                new[] { "AI Meter reads the sessions and limits that your AI apps already save on this PC. Nothing is sent to AI Meter's authors. Choose what it may do; you can change each choice later in Settings.",
                        "AI Meter legge le sessioni e i limiti che le tue app di AI salvano già su questo PC. Agli autori di AI Meter non viene inviato nulla. Scegli cosa può fare; potrai cambiare ogni scelta in Impostazioni.",
                        "AI Meter lee las sesiones y los límites que tus apps de IA ya guardan en este PC. No se envía nada a los autores de AI Meter. Elige qué puede hacer; podrás cambiar cada opción en Configuración.",
                        "AI Meter lit les sessions et les limites que vos apps d’IA enregistrent déjà sur ce PC. Rien n’est envoyé aux auteurs d’AI Meter. Choisissez ce qu’il peut faire ; vous pourrez modifier chaque choix dans les Paramètres.",
                        "AI Meter liest die Sitzungen und Limits, die Ihre KI-Apps bereits auf diesem PC speichern. An die Autoren von AI Meter wird nichts gesendet. Wählen Sie, was es tun darf; jede Auswahl lässt sich später in den Einstellungen ändern.",
                        "O AI Meter lê as sessões e os limites que as suas apps de IA já guardam neste PC. Nada é enviado aos autores do AI Meter. Escolha o que pode fazer; pode alterar cada opção mais tarde nas Configurações." },
                new[] { "Show in the taskbar", "Mostra nella barra delle applicazioni", "Mostrar en la barra de tareas", "Afficher dans la barre des tâches", "In der Taskleiste anzeigen", "Mostrar na barra de tarefas" },
                new[] { "Places a small indicator inside the Windows taskbar. If you turn this off, AI Meter shows a widget on the desktop instead.",
                        "Inserisce un piccolo indicatore nella barra delle applicazioni di Windows. Se lo disattivi, AI Meter mostra invece un widget sul desktop.",
                        "Coloca un pequeño indicador dentro de la barra de tareas de Windows. Si lo desactivas, AI Meter muestra en su lugar un widget en el escritorio.",
                        "Place un petit indicateur dans la barre des tâches de Windows. Si vous le désactivez, AI Meter affiche plutôt un widget sur le bureau.",
                        "Platziert eine kleine Anzeige in der Windows-Taskleiste. Wenn Sie dies ausschalten, zeigt AI Meter stattdessen ein Widget auf dem Desktop.",
                        "Coloca um pequeno indicador na barra de tarefas do Windows. Se o desativar, o AI Meter mostra antes um widget no ambiente de trabalho." },
                new[] { "Privacy policy", "Informativa sulla privacy", "Política de privacidad", "Politique de confidentialité", "Datenschutzerklärung", "Política de privacidade" },
                new[] { "Preview with sample data", "Prova con dati di esempio", "Probar con datos de ejemplo", "Essayer avec des données d’exemple", "Mit Beispieldaten ausprobieren", "Experimentar com dados de exemplo" },
                new[] { "Start", "Inizia", "Empezar", "Commencer", "Starten", "Começar" },
                new[] { "Sample data", "Dati di esempio", "Datos de ejemplo", "Données d’exemple", "Beispieldaten", "Dados de exemplo" },
                new[] { "Show invented figures to preview AI Meter without signing in to any service. Not saved: real data returns at the next start.",
                        "Mostra valori inventati per provare AI Meter senza accedere ad alcun servizio. Non viene salvato: i dati reali tornano al prossimo avvio.",
                        "Muestra cifras inventadas para probar AI Meter sin iniciar sesión en ningún servicio. No se guarda: los datos reales vuelven en el próximo inicio.",
                        "Affiche des valeurs inventées pour essayer AI Meter sans vous connecter à aucun service. Non enregistré : les vraies données reviennent au prochain démarrage.",
                        "Zeigt erfundene Werte, um AI Meter ohne Anmeldung bei einem Dienst auszuprobieren. Wird nicht gespeichert: Beim nächsten Start kehren die echten Daten zurück.",
                        "Mostra valores inventados para experimentar o AI Meter sem iniciar sessão em nenhum serviço. Não é guardado: os dados reais voltam no próximo arranque." },
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
