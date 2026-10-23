using System.Reflection;
using System.Runtime.InteropServices;

// Metadati del file eseguibile (Proprietà → Dettagli). SignPath li verifica su ogni build firmata:
// nome prodotto fisso e la stessa versione in tutti i campi.
[assembly: AssemblyTitle("AI Meter")]
[assembly: AssemblyProduct("AI Meter")]
[assembly: AssemblyDescription("Sessions and usage limits of AI services on Windows")]
[assembly: AssemblyCompany("AI Meter contributors")]
[assembly: AssemblyCopyright("Copyright (c) 2026 AI Meter contributors. MIT License.")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
[assembly: AssemblyInformationalVersion("0.2.0")]
[assembly: ComVisible(false)]
