using System.ComponentModel;

namespace FolderMirror.Services;

/// <summary>Light-weight UI localization. Bind with {Binding [key], Source={x:Static svc:Loc.Instance}}.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly Loc Instance = new();
    public static readonly string[] Languages = { "English", "Deutsch", "Español", "Français", "Română" };
    public event PropertyChangedEventHandler? PropertyChanged;

    int _idx;
    public string Language
    {
        get => Languages[_idx];
        set
        {
            var i = Array.IndexOf(Languages, value);
            _idx = i < 0 ? 0 : i;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public string this[string key] => T(key);

    public static string T(string key)
    {
        if (!Table.TryGetValue(key, out var v)) return key;
        var i = Instance._idx;
        return i < v.Length && !string.IsNullOrEmpty(v[i]) ? v[i] : v[0];
    }

    // order: English, Deutsch, Español, Français, Română
    static readonly Dictionary<string, string[]> Table = new()
    {
        ["nav.dashboard"] = new[] { "Dashboard", "Übersicht", "Panel", "Tableau de bord", "Panou" },
        ["nav.sync"] = new[] { "Synchronization", "Synchronisation", "Sincronización", "Synchronisation", "Sincronizare" },
        ["nav.profiles"] = new[] { "Profiles", "Profile", "Perfiles", "Profils", "Profiluri" },
        ["nav.schedules"] = new[] { "Scheduled Tasks", "Geplante Aufgaben", "Tareas programadas", "Tâches planifiées", "Sarcini programate" },
        ["nav.history"] = new[] { "History", "Verlauf", "Historial", "Historique", "Istoric" },
        ["nav.settings"] = new[] { "Settings", "Einstellungen", "Configuración", "Paramètres", "Setări" },
        ["nav.about"] = new[] { "About", "Info", "Acerca de", "À propos", "Despre" },
        ["tagline"] = new[] { "Your files. Always in sync.", "Ihre Dateien. Immer synchron.", "Tus archivos. Siempre sincronizados.", "Vos fichiers. Toujours synchronisés.", "Fișierele tale. Mereu sincronizate." },
        ["dash.title"] = new[] { "Dashboard", "Übersicht", "Panel", "Tableau de bord", "Panou" },
        ["dash.new"] = new[] { "New Synchronization", "Neue Synchronisation", "Nueva sincronización", "Nouvelle synchronisation", "Sincronizare nouă" },
        ["dash.profiles"] = new[] { "Profiles", "Profile", "Perfiles", "Profils", "Profiluri" },
        ["dash.files"] = new[] { "Files synchronized", "Dateien synchronisiert", "Archivos sincronizados", "Fichiers synchronisés", "Fișiere sincronizate" },
        ["dash.last"] = new[] { "Last synchronization", "Letzte Synchronisation", "Última sincronización", "Dernière synchronisation", "Ultima sincronizare" },
        ["dash.next"] = new[] { "Next scheduled", "Nächste geplante", "Próxima programada", "Prochaine planifiée", "Următoarea programată" },
        ["dash.recent"] = new[] { "Recent activity", "Letzte Aktivität", "Actividad reciente", "Activité récente", "Activitate recentă" },
        ["dash.saved"] = new[] { "Saved profiles", "Gespeicherte Profile", "Perfiles guardados", "Profils enregistrés", "Profiluri salvate" },
        ["dash.status"] = new[] { "Current status", "Aktueller Status", "Estado actual", "État actuel", "Stare curentă" },
        ["btn.run"] = new[] { "Run", "Starten", "Ejecutar", "Lancer", "Rulează" },
        ["btn.edit"] = new[] { "Edit", "Bearbeiten", "Editar", "Modifier", "Editează" },
        ["btn.delete"] = new[] { "Delete", "Löschen", "Eliminar", "Supprimer", "Șterge" },
        ["btn.cancel"] = new[] { "Cancel", "Abbrechen", "Cancelar", "Annuler", "Anulează" },
        ["btn.preview"] = new[] { "Preview", "Vorschau", "Vista previa", "Aperçu", "Previzualizare" },
        ["btn.start"] = new[] { "Start Synchronization", "Synchronisation starten", "Iniciar sincronización", "Démarrer la synchronisation", "Pornește sincronizarea" },
        ["btn.save"] = new[] { "Save", "Speichern", "Guardar", "Enregistrer", "Salvează" },
        ["btn.duplicate"] = new[] { "Duplicate", "Duplizieren", "Duplicar", "Dupliquer", "Duplică" },
        ["btn.new"] = new[] { "New profile", "Neues Profil", "Nuevo perfil", "Nouveau profil", "Profil nou" },
        ["btn.import"] = new[] { "Import", "Importieren", "Importar", "Importer", "Importă" },
        ["btn.export"] = new[] { "Export", "Exportieren", "Exportar", "Exporter", "Exportă" },
        ["status.idle"] = new[] { "Idle – ready", "Bereit", "Inactivo – listo", "Inactif – prêt", "Inactiv – gata" },
        ["sync.title"] = new[] { "Synchronization", "Synchronisation", "Sincronización", "Synchronisation", "Sincronizare" },
        ["profiles.title"] = new[] { "Profiles", "Profile", "Perfiles", "Profils", "Profiluri" },
        ["sched.title"] = new[] { "Scheduled Tasks", "Geplante Aufgaben", "Tareas programadas", "Tâches planifiées", "Sarcini programate" },
        ["hist.title"] = new[] { "History", "Verlauf", "Historial", "Historique", "Istoric" },
        ["settings.title"] = new[] { "Settings", "Einstellungen", "Configuración", "Paramètres", "Setări" },
        ["about.title"] = new[] { "About", "Info", "Acerca de", "À propos", "Despre" },
        ["tray.open"] = new[] { "Open Folder Mirror", "Folder Mirror öffnen", "Abrir Folder Mirror", "Ouvrir Folder Mirror", "Deschide Folder Mirror" },
        ["tray.run"] = new[] { "Run profile", "Profil ausführen", "Ejecutar perfil", "Lancer un profil", "Rulează profil" },
        ["tray.status"] = new[] { "View synchronization status", "Synchronisationsstatus anzeigen", "Ver estado de sincronización", "Afficher l'état", "Vezi starea sincronizării" },
        ["tray.pause"] = new[] { "Pause scheduled tasks", "Geplante Aufgaben pausieren", "Pausar tareas programadas", "Suspendre les tâches planifiées", "Suspendă sarcinile programate" },
        ["tray.exit"] = new[] { "Exit", "Beenden", "Salir", "Quitter", "Ieșire" },
    };
}
