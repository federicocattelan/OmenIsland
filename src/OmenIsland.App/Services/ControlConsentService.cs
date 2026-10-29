using System.Windows;

namespace OmenIsland.App.Services;

public sealed class ControlConsentService : IControlConsentService
{
    private readonly SettingsStore _settings;

    public ControlConsentService(SettingsStore settings)
    {
        _settings = settings;
    }

    public bool HasAcceptedControl =>
        _settings.Current.HardwareControlAccepted;

    public async Task<bool> EnsureConsentAsync()
    {
        if (_settings.Current.HardwareControlAccepted)
        {
            return true;
        }

        var result = System.Windows.MessageBox.Show(
            """
            Omen Island sta per modificare profilo termico e ventole
            attraverso l'interfaccia HP WMI del firmware.

            Le impostazioni vengono limitate a valori sicuri e le ventole
            tornano su Auto alla chiusura. Vuoi abilitare i controlli?
            """,
            "Abilita controllo hardware",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return false;
        }

        _settings.Current.HardwareControlAccepted = true;
        await _settings.SaveAsync();
        return true;
    }
}
