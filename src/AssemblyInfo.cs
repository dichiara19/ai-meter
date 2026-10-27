using System.Reflection;
using System.Runtime.InteropServices;

// Metadati del file eseguibile (Proprietà → Dettagli). SignPath li verifica su ogni build firmata:
// nome prodotto fisso e la stessa versione in tutti i campi.
[assembly: AssemblyTitle("AI Meter")]
[assembly: AssemblyProduct("AI Meter")]
[assembly: AssemblyDescription("Sessions and usage limits of AI services on Windows")]
[assembly: AssemblyCompany("Giuseppe Gabriele Di Chiara")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Giuseppe Gabriele Di Chiara. Apache License 2.0.")]
[assembly: AssemblyVersion("0.7.0.0")]
[assembly: AssemblyFileVersion("0.7.0.0")]
[assembly: AssemblyInformationalVersion("0.7.0")]
[assembly: ComVisible(false)]
