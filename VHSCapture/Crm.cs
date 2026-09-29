using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VHSCapture
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Collegamento con il CRM (Controllo PC → 🎬 VHSCapture): lavori in coda, inizio/fine di ogni
    // cassetta, stato dal vivo della postazione, configurazione di questo PC.
    // Il CRM non si collega mai al PC: è VHSCapture che chiama il CRM sull'indirizzo pubblico con il
    // token della postazione. Se la rete salta, gli eventi (inizio, fine, pronto) restano in una coda
    // salvata su disco e partono appena il CRM risponde: la registrazione non si ferma mai.
    // Ogni evento ha un codice univoco: se viene rimandato, il CRM non lo conta due volte.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class CrmLavoro
    {
        public int id { get; set; }
        public string cliente { get; set; } = "";
        public string stato { get; set; } = "";
        public int nastri_fatti { get; set; }
        public int nastri_totali { get; set; }
        public int prossima { get; set; } = 1;
        public int supporti_fatti { get; set; }
        public int supporti_totali { get; set; }
        public string dettaglio { get; set; } = "";
        public string cartella { get; set; } = "";
        public string in_registrazione_su { get; set; } = "";
        public override string ToString() => cliente;
    }

    public class CrmPostazione
    {
        public int id { get; set; }
        public string nome { get; set; } = "";
        public bool configurata { get; set; }
        public bool online { get; set; }
        public bool registrando { get; set; }
        public bool questa { get; set; }
        public string cliente { get; set; } = "";
        public int? cassetta_n { get; set; }
        public int? nastri_totali { get; set; }
        public int secondi { get; set; }
        public bool in_pausa { get; set; }
    }

    public class CrmConfig
    {
        public bool chiedi_cliente { get; set; } = true;
        public bool cartella_cliente { get; set; } = true;
        public bool chiedi_fine { get; set; } = true;
        public bool durata_minima_attiva { get; set; } = true;
        public int durata_minima_min { get; set; } = 4;
        public string cartella_base { get; set; } = "";
    }

    public class CrmFine
    {
        public int nastri_fatti { get; set; }
        public int nastri_totali { get; set; }
        public int prossima { get; set; }
        public bool nastri_finiti { get; set; }
        public bool tutto_finito { get; set; }
        public int restano_altri { get; set; }
        public string dettaglio { get; set; } = "";
        public string cliente { get; set; } = "";
        public string stato { get; set; } = "";          // stato della scheda dopo questa cassetta (in attesa / in lavorazione / pronto)
    }

    public sealed class CrmClient : IDisposable
    {
        readonly AppSettings s;
        readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        static readonly string CodaFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VHSCapture", "crm-coda.json");

        class Evento { public string Percorso { get; set; } = ""; public string Corpo { get; set; } = ""; }
        List<Evento> coda = new List<Evento>();
        readonly object lockCoda = new object();

        public string NomePostazione { get; set; } = "";
        public bool Raggiungibile { get; private set; }
        public string UltimoErrore { get; private set; } = "";
        public int InCoda { get { lock (lockCoda) return coda.Count; } }
        public string Versione { get; set; } = "";

        public CrmClient(AppSettings settings)
        {
            s = settings;
            try { if (File.Exists(CodaFile)) coda = JsonSerializer.Deserialize<List<Evento>>(File.ReadAllText(CodaFile)) ?? new List<Evento>(); } catch { coda = new List<Evento>(); }
        }

        /// <summary>Collegamento acceso nelle Impostazioni e indirizzo/token compilati. «Prova collegamento» funziona anche da spento.</summary>
        public bool Configurato => s.CrmAttivo && !string.IsNullOrWhiteSpace(s.CrmUrl) && (s.CrmToken ?? "").Trim().Length >= 20;

        string Base => (s.CrmUrl ?? "").Trim().TrimEnd('/');

        HttpRequestMessage Req(HttpMethod m, string percorso, object corpo = null, string url = null, string token = null)
        {
            var r = new HttpRequestMessage(m, (url ?? Base) + "/api/cattura/" + percorso);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (token ?? s.CrmToken ?? "").Trim());
            if (corpo != null) r.Content = new StringContent(corpo is string str ? str : JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
            return r;
        }

        async Task<T> Chiama<T>(HttpMethod m, string percorso, object corpo = null) where T : class
        {
            if (!Configurato) return null;
            try
            {
                using var r = await http.SendAsync(Req(m, percorso, corpo));
                string testo = await r.Content.ReadAsStringAsync();
                if (!r.IsSuccessStatusCode)
                {
                    Raggiungibile = r.StatusCode != System.Net.HttpStatusCode.Unauthorized && (int)r.StatusCode < 500;
                    UltimoErrore = (int)r.StatusCode == 401 ? "token non valido o postazione scollegata dal CRM" : $"il CRM ha risposto {(int)r.StatusCode}";
                    return null;
                }
                Raggiungibile = true; UltimoErrore = "";
                return JsonSerializer.Deserialize<T>(testo, Json);
            }
            catch (Exception ex)
            {
                Raggiungibile = false;
                UltimoErrore = ex is TaskCanceledException ? "il CRM non risponde" : "CRM non raggiungibile (" + ex.Message + ")";
                return null;
            }
        }

        /// <summary>Prova il collegamento con indirizzo e token scritti nelle Impostazioni (anche prima di salvarli).</summary>
        public async Task<(bool ok, string msg)> Prova(string url, string token)
        {
            try
            {
                using var r = await http.SendAsync(Req(HttpMethod.Get, "ping", null, (url ?? "").Trim().TrimEnd('/'), token));
                string testo = await r.Content.ReadAsStringAsync();
                if ((int)r.StatusCode == 401) return (false, "Il CRM ha rifiutato il token: generane uno nuovo in Controllo PC → 🎬 VHSCapture.");
                if (!r.IsSuccessStatusCode) return (false, $"Il CRM ha risposto {(int)r.StatusCode}.");
                using var d = JsonDocument.Parse(testo);
                return (true, "Collegato: questo PC è «" + d.RootElement.GetProperty("postazione").GetString() + "» nel CRM.");
            }
            catch (Exception ex) { return (false, "CRM non raggiungibile: " + ex.Message); }
        }

        class RispostaLavori { public string postazione { get; set; } = ""; public List<CrmLavoro> lavori { get; set; } = new List<CrmLavoro>(); }

        /// <summary>Lavori in coda da riversare (in attesa / in lavorazione), in ordine di arrivo. null = CRM non raggiungibile.</summary>
        public async Task<List<CrmLavoro>> Lavori()
        {
            var r = await Chiama<RispostaLavori>(HttpMethod.Get, "lavori");
            if (r == null) return null;
            NomePostazione = r.postazione ?? "";
            return r.lavori ?? new List<CrmLavoro>();
        }

        /// <summary>A che punto sono tutti i PC di riversaggio (compreso questo).</summary>
        public Task<List<CrmPostazione>> Postazioni() => Chiama<List<CrmPostazione>>(HttpMethod.Get, "altre-postazioni");

        class RispostaConfig { public CrmConfig config { get; set; } public string aggiornata_il { get; set; } = ""; }

        public async Task<(CrmConfig cfg, string quando)?> LeggiConfig()
        {
            var r = await Chiama<RispostaConfig>(HttpMethod.Get, "config");
            if (r?.config == null) return null;
            return (r.config, r.aggiornata_il ?? "");
        }

        public async Task<bool> SalvaConfig(CrmConfig cfg, string quando)
            => await Chiama<RispostaConfig>(HttpMethod.Put, "config", new { config = cfg, aggiornata_il = quando }) != null;

        class RispostaBattito { public string config_aggiornata_il { get; set; } = ""; }

        /// <summary>Battito (~20 s): tiene la postazione «collegata» nel CRM. Restituisce quando è cambiata la configurazione.</summary>
        public async Task<string> Battito(bool registrando, int vhsId, int cassetta, int secondi, bool inPausa)
        {
            var r = await Chiama<RispostaBattito>(HttpMethod.Post, "stato", new
            {
                registrando, vhs_id = registrando ? vhsId : 0, cassetta_n = registrando ? cassetta : 0,
                secondi, in_pausa = inPausa, versione = Versione,
            });
            return r?.config_aggiornata_il;
        }

        // ── eventi che contano (inizio, fine, pronto): coda su disco se il CRM non risponde ──

        void SalvaCoda() { try { Directory.CreateDirectory(Path.GetDirectoryName(CodaFile)); File.WriteAllText(CodaFile, JsonSerializer.Serialize(coda)); } catch { } }

        static Dictionary<string, object> ConEvento(Dictionary<string, object> corpo)
        {
            if (!corpo.ContainsKey("evento_id")) corpo["evento_id"] = Guid.NewGuid().ToString("N");
            return corpo;
        }

        /// <summary>Manda subito; se il CRM non risponde lo mette in coda (partirà da solo). Restituisce la risposta o null.</summary>
        public async Task<string> Manda(string percorso, Dictionary<string, object> corpo)
        {
            string json = JsonSerializer.Serialize(ConEvento(corpo));
            await Svuota();                      // prima quelli rimasti indietro, in ordine
            if (InCoda == 0 && Configurato)
            {
                try
                {
                    using var r = await http.SendAsync(Req(HttpMethod.Post, percorso, json));
                    string testo = await r.Content.ReadAsStringAsync();
                    if (r.IsSuccessStatusCode) { Raggiungibile = true; UltimoErrore = ""; return testo; }
                    if ((int)r.StatusCode >= 400 && (int)r.StatusCode < 500 && (int)r.StatusCode != 401 && (int)r.StatusCode != 408 && (int)r.StatusCode != 429)
                    { UltimoErrore = $"il CRM ha rifiutato «{percorso}» ({(int)r.StatusCode})"; return null; }   // es. scheda eliminata: inutile riprovare
                }
                catch (Exception ex) { Raggiungibile = false; UltimoErrore = "CRM non raggiungibile (" + ex.Message + ")"; }
            }
            lock (lockCoda) { coda.Add(new Evento { Percorso = percorso, Corpo = json }); SalvaCoda(); }
            return null;
        }

        /// <summary>Manda gli eventi rimasti in coda, nell'ordine in cui sono nati. Si ferma al primo che non passa.</summary>
        public async Task Svuota()
        {
            if (!Configurato) return;
            while (true)
            {
                Evento e;
                lock (lockCoda) { if (coda.Count == 0) return; e = coda[0]; }
                try
                {
                    using var r = await http.SendAsync(Req(HttpMethod.Post, e.Percorso, e.Corpo));
                    bool scartare = (int)r.StatusCode >= 400 && (int)r.StatusCode < 500 && (int)r.StatusCode != 401 && (int)r.StatusCode != 408 && (int)r.StatusCode != 429;
                    if (!r.IsSuccessStatusCode && !scartare) { Raggiungibile = (int)r.StatusCode != 401; return; }
                    Raggiungibile = true;
                }
                catch { Raggiungibile = false; return; }
                lock (lockCoda) { if (coda.Count > 0 && ReferenceEquals(coda[0], e)) coda.RemoveAt(0); SalvaCoda(); }
            }
        }

        public static T Leggi<T>(string json) where T : class
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Json); } catch { return null; }
        }

        public void Dispose() => http.Dispose();
    }
}
