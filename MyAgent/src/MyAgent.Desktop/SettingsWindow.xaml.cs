using System.Windows;
using System.Windows.Controls;

namespace MyAgent.Desktop;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        var settings = vm.Settings;

        foreach (ComboBoxItem item in ProviderBox.Items)
        {
            if ((string)item.Tag == settings.Provider)
            {
                ProviderBox.SelectedItem = item;
                break;
            }
        }
        ApiKeyBox.Password = settings.ApiKey;
        BaseUrlBox.Text = settings.BaseUrl;
        ThinkingBox.IsChecked = settings.Thinking;
        foreach (ComboBoxItem item in EffortBox.Items)
        {
            if ((string)item.Content == settings.Effort)
            {
                EffortBox.SelectedItem = item;
                break;
            }
        }
        foreach (ComboBoxItem item in ThemeBox.Items)
        {
            if ((string)item.Tag == settings.Theme)
            {
                ThemeBox.SelectedItem = item;
                break;
            }
        }
        RefreshModels(settings.Model);
    }

    private string SelectedProvider =>
        (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "mock";

    private void RefreshModels(string? selected)
    {
        ModelBox.Items.Clear();
        var settings = _vm.Settings;
        var models = settings.ModelCatalogProvider == SelectedProvider && settings.ModelCatalog.Count > 0
            ? settings.ModelCatalog.Select(id => (Id: id, Name: id)).ToList()
            : DefaultModels(SelectedProvider);
        foreach (var (id, name) in models)
            ModelBox.Items.Add(new ModelOptionVm { Id = id, Name = name });
        ModelBox.SelectedValue = models.FirstOrDefault(m => m.Id == selected).Id ?? models[0].Id;
    }

    private static List<(string Id, string Name)> DefaultModels(string provider) => provider switch
    {
        "openai-compat" => [("deepseek-chat", "Chat"), ("deepseek-reasoner", "Reasoner")],
        "mock" => [("mock", "Mock (keyless demo)")],
        _ => [(MyAgent.Llm.ModelIds.Flash, "Flash"), (MyAgent.Llm.ModelIds.V4Pro, "V4 Pro")],
    };

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox is not null) RefreshModels(null);
        if (ModelListStatus is not null) SetModelStatus("", false);
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProvider == "mock")
        {
            SetModelStatus("Mock 服务商使用内置模型，无需获取。", false);
            return;
        }
        FetchModelsBtn.IsEnabled = false;
        SetModelStatus("正在获取模型列表…", false);
        try
        {
            var models = await MyAgent.Llm.RemoteModelList.FetchAsync(SelectedProvider, BaseUrlBox.Text, ApiKeyBox.Password);
            var selected = ModelBox.SelectedValue as string;
            ModelBox.Items.Clear();
            foreach (var model in models)
                ModelBox.Items.Add(new ModelOptionVm { Id = model.Id, Name = model.Name });
            ModelBox.SelectedValue = models.Any(m => m.Id == selected) ? selected : models[0].Id;
            SetModelStatus($"已获取 {models.Count} 个模型，保存后生效。", false);
        }
        catch (Exception ex)
        {
            SetModelStatus(ex.Message, true);
        }
        finally
        {
            FetchModelsBtn.IsEnabled = true;
        }
    }

    private void SetModelStatus(string text, bool isError)
    {
        ModelListStatus.Text = text;
        ModelListStatus.SetResourceReference(ForegroundProperty, isError ? "Danger" : "TextTertiary");
    }

    private void NavModel_Click(object sender, RoutedEventArgs e)
    {
        ModelPanel.Visibility = Visibility.Visible;
        AppearancePanel.Visibility = Visibility.Collapsed;
    }

    private void NavAppearance_Click(object sender, RoutedEventArgs e)
    {
        ModelPanel.Visibility = Visibility.Collapsed;
        AppearancePanel.Visibility = Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.UpdateSettings(new
        {
            provider = SelectedProvider,
            apiKey = ApiKeyBox.Password,
            baseUrl = BaseUrlBox.Text,
            model = (ModelBox.SelectedValue as string) ?? _vm.Settings.Model,
            thinking = ThinkingBox.IsChecked == true,
            effort = (EffortBox.SelectedItem as ComboBoxItem)?.Content as string ?? "high",
            theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system",
            modelCatalog = ModelBox.Items.OfType<ModelOptionVm>().Select(m => m.Id).ToList(),
            modelCatalogProvider = SelectedProvider,
        });
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}