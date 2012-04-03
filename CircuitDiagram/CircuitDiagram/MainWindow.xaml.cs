﻿// MainWindow.xaml.cs
//
// Circuit Diagram http://www.circuit-diagram.org/
//
// Copyright (C) 2012  Sam Fisher
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using CircuitDiagram.Components;
using CircuitDiagram.Render;
using Microsoft.Win32;
using System.Windows.Threading;
using CircuitDiagram.IO;

namespace CircuitDiagram
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Variables
        string m_documentTitle;
        string m_docPath = "";
        DispatcherTimer m_statusTimer;
        UndoManager m_undoManager;
        Dictionary<Key, string> m_toolboxShortcuts = new Dictionary<Key, string>();

        UndoManager UndoManager { get { return m_undoManager; } }
        public System.Collections.ObjectModel.ObservableCollection<string> RecentFiles = new System.Collections.ObjectModel.ObservableCollection<string>();

        List<ImplementationConversionCollection> m_componentRepresentations = new List<ImplementationConversionCollection>();
        #endregion

        public MainWindow()
        {
            InitializeComponent();

            m_statusTimer = new DispatcherTimer(new TimeSpan(0, 0, 5), DispatcherPriority.Normal, new EventHandler((sender, e) =>
                {
                    m_statusTimer.Stop(); lblStatus.Text = "Ready";
                }), lblStatus.Dispatcher);

            // Initialize cdlibrary
            ConfigureCdLibrary();

            // Initialize settings
#if PORTABLE
            CircuitDiagram.Settings.Settings.Initialize(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\settings\\settings.xml");
#else
            CircuitDiagram.Settings.Settings.Initialize(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Circuit Diagram\\settings.xml");
#endif
            ApplySettings();

            ComponentHelper.ComponentUpdatedDelegate = new ComponentUpdatedDelegate(Editor_ComponentUpdated);
            ComponentHelper.CreateEditor = new CreateComponentEditorDelegate(ComponentEditorHelper.CreateEditor);
            ComponentHelper.LoadIcon = new LoadIconDelegate(
                (buffer, type) =>
                {
                    MemoryStream tempStream = new MemoryStream(buffer);
                    var tempIcon = new System.Windows.Media.Imaging.BitmapImage();
                    tempIcon.BeginInit();
                    tempIcon.StreamSource = tempStream;
                    tempIcon.EndInit();
                    return tempIcon;
                });

            circuitDisplay.Document = new CircuitDocument();
            Load();

            // load recent items
            LoadRecentFiles();
            mnuRecentItems.ItemsSource = RecentFiles;

            // Initialize undo manager
            m_undoManager = new UndoManager();
            circuitDisplay.UndoManager = m_undoManager;
            m_undoManager.ActionDelegate = new CircuitDiagram.UndoManager.UndoActionDelegate(UndoActionProcessor);
            m_undoManager.ActionOccurred += new EventHandler(m_undoManager_ActionOccurred);

            this.Title = "Untitled - Circuit Diagram";
            m_documentTitle = "Untitled";

            this.Closed += new EventHandler(MainWindow_Closed);
            this.Loaded += new RoutedEventHandler(MainWindow_Loaded);

            // check if should open file
            if (App.AppArgs.Length > 0)
            {
                if (System.IO.File.Exists(App.AppArgs[0]))
                    OpenDocument(App.AppArgs[0]);
            }
        }

        void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // check for updates
            if (!Settings.Settings.HasSetting("CheckForUpdatesOnStartup"))
                Settings.Settings.Write("CheckForUpdatesOnStartup", true);
            if (!Settings.Settings.ReadBool("CheckForUpdatesOnStartup"))
                return;
            if (DateTime.Now.Subtract(Settings.Settings.HasSetting("LastCheckForUpdates") ? (DateTime)Settings.Settings.Read("LastCheckForUpdates") : new DateTime(2000, 1, 1)).TotalDays < 1.0)
                return;
            System.Threading.Thread T = new System.Threading.Thread((O => CheckForUpdates(false)));
            T.Start();
        }

        void MainWindow_Closed(object sender, EventArgs e)
        {
            Settings.Settings.Save();
        }

        /// <summary>
        /// Apply settings including toolbox and connection points.
        /// </summary>
        private void ApplySettings()
        {
            toolboxScroll.VerticalScrollBarVisibility = (CircuitDiagram.Settings.Settings.ReadBool("showToolboxScrollBar") ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
            circuitDisplay.ShowConnectionPoints = Settings.Settings.ReadBool("showConnectionPoints");
        }

        /// <summary>
        /// Configures cdlibrary.dll, setting application constants.
        /// </summary>
        private void ConfigureCdLibrary()
        {
            System.Reflection.Assembly _assemblyInfo = System.Reflection.Assembly.GetExecutingAssembly();
            string theVersion = string.Empty;
            if (_assemblyInfo != null)
                theVersion = _assemblyInfo.GetName().Version.ToString();
            BuildChannelAttribute channelAttribute = _assemblyInfo.GetCustomAttributes(typeof(BuildChannelAttribute), false).FirstOrDefault(item => item is BuildChannelAttribute) as BuildChannelAttribute;
            if (channelAttribute != null && channelAttribute.Type == BuildChannelAttribute.ChannelType.Dev && channelAttribute.DisplayName != null)
                theVersion += " " + channelAttribute.DisplayName;
            CircuitDiagram.IO.ApplicationInfo.FullName = "Circuit Diagram " + theVersion;
        }

        /// <summary>
        /// Open a document and add it to the recent files menu.
        /// </summary>
        /// <param name="path">The path of the document to open.</param>
        private void OpenDocument(string path)
        {
            if (System.IO.Path.GetExtension(path) == ".cddx" || System.IO.Path.GetExtension(path) == ".zip")
            {
                CircuitDocument document;
                CircuitDiagram.IO.DocumentLoadResult result = CircuitDiagram.IO.CDDX.CDDXIO.Read(File.OpenRead(path), out document);

                if (result.Type != DocumentLoadResultType.Success || result.Errors.Count > 0 || result.UnavailableComponents.Count > 0)
                {
                    winDocumentLoadResult loadResultWindow = new winDocumentLoadResult();
                    loadResultWindow.Owner = this;
                    loadResultWindow.SetMessage(result.Type);
                    loadResultWindow.SetErrors(result.Errors);
                    loadResultWindow.SetUnavailableComponents(result.UnavailableComponents);
                    loadResultWindow.ShowDialog();
                }

                if (result.Type == DocumentLoadResultType.Success || result.Type == DocumentLoadResultType.SuccessNewerVersion)
                {
                    circuitDisplay.Document = document;
                    circuitDisplay.DrawConnections();
                    m_docPath = path;
                    m_documentTitle = System.IO.Path.GetFileNameWithoutExtension(path);
                    this.Title = m_documentTitle + " - Circuit Diagram";
                    m_undoManager = new UndoManager();
                    circuitDisplay.UndoManager = m_undoManager;
                    m_undoManager.ActionDelegate = new CircuitDiagram.UndoManager.UndoActionDelegate(UndoActionProcessor);
                    m_undoManager.ActionOccurred += new EventHandler(m_undoManager_ActionOccurred);
                }
            }
            else
            {
                CircuitDiagram.IO.CircuitDocumentLoader loader = new IO.CircuitDocumentLoader();

                CircuitDiagram.IO.DocumentLoadResult result = loader.Load(File.OpenRead(path));

                if (result.Type != DocumentLoadResultType.Success || result.Errors.Count > 0 || result.UnavailableComponents.Count > 0)
                {
                    winDocumentLoadResult loadResultWindow = new winDocumentLoadResult();
                    loadResultWindow.Owner = this;
                    loadResultWindow.SetMessage(result.Type);
                    loadResultWindow.SetUnavailableComponents(result.UnavailableComponents);
                    loadResultWindow.ShowDialog();
                }

                if (result.Type == IO.DocumentLoadResultType.Success)
                {
                    circuitDisplay.Document = loader.Document;
                    circuitDisplay.DrawConnections();

                    m_docPath = path;
                    m_documentTitle = System.IO.Path.GetFileNameWithoutExtension(path);
                    this.Title = m_documentTitle + " - Circuit Diagram";
                    m_undoManager = new UndoManager();
                    circuitDisplay.UndoManager = m_undoManager;
                    m_undoManager.ActionDelegate = new CircuitDiagram.UndoManager.UndoActionDelegate(UndoActionProcessor);
                    m_undoManager.ActionOccurred += new EventHandler(m_undoManager_ActionOccurred);
                }
            }
        }

        /// <summary>
        /// Load component descriptions from disk and populate the toolbox.
        /// </summary>
        private void Load()
        {
            if (m_defaultSaveOptions == null)
                LoadDefaultCDDXSaveOptions();

            #region Load component descriptions
            bool conflictingGuid = false;
            List<string> componentLocations = new List<string>();

            string permanentComponentsDirectory = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\ext";
            if (Directory.Exists(permanentComponentsDirectory))
                componentLocations.Add(permanentComponentsDirectory);
            string userComponentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Circuit Diagram\\components";
            if (Directory.Exists(userComponentsDirectory))
                componentLocations.Add(userComponentsDirectory);

#if DEBUG
            string debugComponentsDirectory = Path.GetFullPath("../../Components");
            if (Directory.Exists(debugComponentsDirectory))
                componentLocations.Add(debugComponentsDirectory);
#endif

#if PORTABLE
            string portableComponentsDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\components";
            if (Directory.Exists(portableComponentsDirectory))
                componentLocations.Add(portableComponentsDirectory);
#endif

            CircuitDiagram.IO.XmlLoader xmlLoader = new CircuitDiagram.IO.XmlLoader();
            CircuitDiagram.IO.BinaryLoader binLoader = new CircuitDiagram.IO.BinaryLoader();

            foreach (string location in componentLocations)
            {
                foreach (string file in System.IO.Directory.GetFiles(location, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (xmlLoader.Load(fs))
                        {
                            ComponentDescription description = xmlLoader.GetDescriptions()[0];
                            description.Metadata.Location = ComponentDescriptionMetadata.LocationType.Installed;
                            description.Source = new ComponentDescriptionSource(file, new System.Collections.ObjectModel.ReadOnlyCollection<ComponentDescription>(new ComponentDescription[] { description }));

                            // Check if duplicate GUID
                            if (!conflictingGuid && description.Metadata.GUID != Guid.Empty)
                            {
                                foreach (ComponentDescription compareDescription in ComponentHelper.ComponentDescriptions)
                                    if (compareDescription.Metadata.GUID == description.Metadata.GUID)
                                        conflictingGuid = true;
                            }

                            ComponentHelper.AddDescription(description);
                            if (ComponentHelper.WireDescription == null && description.ComponentName.ToLowerInvariant() == "wire" && description.Metadata.GUID == new Guid("6353882b-5208-4f88-a83b-2271cc82b94f"))
                                ComponentHelper.WireDescription = description;
                        }
                    }
                }
            }
            Stream keyStream = System.Reflection.Assembly.GetAssembly(typeof(CircuitDocument)).GetManifestResourceStream("CircuitDiagram.key.txt");
            System.Security.Cryptography.RSACryptoServiceProvider tempRSA = new System.Security.Cryptography.RSACryptoServiceProvider();
            byte[] data = new byte[keyStream.Length];
            keyStream.Read(data, 0, (int)keyStream.Length);
            string aaa = Encoding.UTF8.GetString(data);
            tempRSA.FromXmlString(aaa.Trim());
            foreach (string location in componentLocations)
            {
                foreach (string file in System.IO.Directory.GetFiles(location, "*.cdcom", SearchOption.TopDirectoryOnly))
                {
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (binLoader.Load(fs, tempRSA.ExportParameters(false)))
                        {
                            ComponentDescription[] descriptions = binLoader.GetDescriptions();
                            ComponentDescriptionSource source = new ComponentDescriptionSource(file, new System.Collections.ObjectModel.ReadOnlyCollection<ComponentDescription>(descriptions));
                            foreach (ComponentDescription description in descriptions)
                            {
                                description.Metadata.Location = ComponentDescriptionMetadata.LocationType.Installed;
                                description.Source = source;

                                // Check if duplicate GUID
                                if (!conflictingGuid && description.Metadata.GUID != Guid.Empty)
                                {
                                    foreach (ComponentDescription compareDescription in ComponentHelper.ComponentDescriptions)
                                        if (compareDescription.Metadata.GUID == description.Metadata.GUID)
                                            conflictingGuid = true;
                                }

                                ComponentHelper.AddDescription(description);
                                if (ComponentHelper.WireDescription == null && description.ComponentName.ToLowerInvariant() == "wire" && description.Metadata.GUID == new Guid("6353882b-5208-4f88-a83b-2271cc82b94f"))
                                    ComponentHelper.WireDescription = description;
                            }
                        }
                    }
                }
            }

            if (conflictingGuid)
                SetStatusText("Two or more components have the same GUID.");
            #endregion

            LoadToolbox();

            #region Load Component Implementation Conversions
#if PORTABLE
            string implementationsFileLocation = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\settings\\implementations.xml";
#else
            string implementationsFileLocation = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Circuit Diagram\\implementations.xml";
#endif

            if (File.Exists(implementationsFileLocation))
            {
                try
                {
                    XmlDocument implDoc = new XmlDocument();
                    implDoc.Load(implementationsFileLocation);

                    XmlNodeList sourceNodes = implDoc.SelectNodes("/implementations/source");
                    foreach (XmlNode sourceNode in sourceNodes)
                    {
                        string collection = sourceNode.Attributes["definitions"].InnerText;

                        ImplementationConversionCollection newCollection = new ImplementationConversionCollection();
                        newCollection.ImplementationSet = collection;

                        foreach (XmlNode childNode in sourceNode.ChildNodes)
                        {
                            if (childNode.Name != "add")
                                continue;

                            string item = childNode.Attributes["item"].InnerText;
                            Guid guid = Guid.Empty;
                            XmlNode guidNode = childNode.SelectSingleNode("guid");
                            if (guidNode != null)
                                guid = new Guid(guidNode.InnerText);
                            string configuration = null;
                            XmlNode configurationNode = childNode.SelectSingleNode("configuration");
                            if (configurationNode != null)
                                configuration = configurationNode.InnerText;

                            ComponentDescription description = ComponentHelper.FindDescription(guid);
                            if (description != null)
                            {
                                ImplementationConversion newConversion = new ImplementationConversion();
                                newConversion.ImplementationName = item;
                                newConversion.ToName = description.ComponentName;
                                newConversion.ToGUID = description.Metadata.GUID;

                                ComponentConfiguration theConfiguration = null;
                                if (configuration != null)
                                {
                                    theConfiguration = description.Metadata.Configurations.FirstOrDefault(check => check.Name == configuration);
                                    if (theConfiguration != null)
                                    {
                                        newConversion.ToConfiguration = theConfiguration.Name;
                                        newConversion.ToIcon = theConfiguration.Icon as ImageSource;
                                    }
                                    else
                                        newConversion.ToIcon = description.Metadata.Icon as ImageSource;
                                }
                                else
                                    newConversion.ToIcon = description.Metadata.Icon as ImageSource;

                                newCollection.Items.Add(newConversion);
                                ComponentHelper.SetStandardComponent(newCollection.ImplementationSet, newConversion.ImplementationName, description, theConfiguration);
                            }
                        }

                        m_componentRepresentations.Add(newCollection);
                    }
                }
                catch (Exception)
                {
                    // Invalid XML file
                }
            }
            #endregion
        }

        /// <summary>
        /// Populates the toolbox from the xml file.
        /// </summary>
        private void LoadToolbox()
        {
            object selectCategory = mainToolbox.Items[0];
            mainToolbox.Items.Clear();
            mainToolbox.Items.Add(selectCategory);

            m_toolboxShortcuts.Clear();

            try
            {
                XmlDocument toolboxSettings = new XmlDocument();
#if PORTABLE
                string toolboxSettingsPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\settings\\toolbox.xml";
#else
                string toolboxSettingsPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Circuit Diagram\\toolbox.xml";
#endif
                toolboxSettings.Load(toolboxSettingsPath);

                XmlNodeList categoryNodes = toolboxSettings.SelectNodes("/display/category");
                foreach (XmlNode categoryNode in categoryNodes)
                {
                    var newCategory = new Toolbox.ToolboxCategory();
                    foreach (XmlNode node in categoryNode.ChildNodes)
                    {
                        if (node.Name == "component")
                        {
                            XmlElement element = node as XmlElement;

                            if (element.HasAttribute("guid") && element.HasAttribute("configuration"))
                            {
                                ComponentDescription description = ComponentHelper.FindDescription(new Guid(element.Attributes["guid"].InnerText));
                                if (description != null)
                                {
                                    ComponentConfiguration configuration = description.Metadata.Configurations.FirstOrDefault(configItem => configItem.Name == element.Attributes["configuration"].InnerText);
                                    if (configuration != null)
                                    {
                                        Toolbox.ToolboxItem newItem = new Toolbox.ToolboxItem();
                                        newItem.Tag = "@rid:" + description.RuntimeID + ", @config: " + configuration.Name;
                                        newItem.ToolTip = configuration.Name;
                                        TextBlock contentBlock = new TextBlock();
                                        contentBlock.Text = configuration.Name;
                                        contentBlock.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                                        contentBlock.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                                        newItem.Content = contentBlock;
                                        if (configuration.Icon != null)
                                        {
                                            Canvas contentCanvas = new Canvas();
                                            contentCanvas.Width = 45;
                                            contentCanvas.Height = 45;
                                            var newImage = new Image() { Width = 45, Height = 45, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = System.Windows.VerticalAlignment.Center, Source = configuration.Icon as ImageSource };
                                            newImage.Effect = new System.Windows.Media.Effects.DropShadowEffect();
                                            newImage.SetValue(System.Windows.Media.RenderOptions.BitmapScalingModeProperty, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                                            contentCanvas.Children.Add(newImage);
                                            newItem.Content = contentCanvas;
                                        }
                                        else if (description.Metadata.Icon != null)
                                        {
                                            Canvas contentCanvas = new Canvas();
                                            contentCanvas.Width = 45;
                                            contentCanvas.Height = 45;
                                            var newImage = new Image() { Width = 45, Height = 45, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = System.Windows.VerticalAlignment.Center, Source = description.Metadata.Icon as ImageSource };
                                            newImage.Effect = new System.Windows.Media.Effects.DropShadowEffect();
                                            newImage.SetValue(System.Windows.Media.RenderOptions.BitmapScalingModeProperty, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                                            contentCanvas.Children.Add(newImage);
                                            newItem.Content = contentCanvas;
                                        }
                                        newItem.Click += new RoutedEventHandler(toolboxButton_Click);
                                        newCategory.Items.Add(newItem);

                                        // Shortcut
                                        if (element.HasAttribute("key") && KeyTextConverter.IsValidLetterKey(element.Attributes["key"].InnerText))
                                        {
                                            Key key = (Key)Enum.Parse(typeof(Key), element.Attributes["key"].InnerText);

                                            if (!m_toolboxShortcuts.ContainsKey(key))
                                                m_toolboxShortcuts.Add(key, "@rid:" + description.RuntimeID + ", @config: " + configuration.Name);
                                        }
                                    }
                                }
                            }
                            else if (element.HasAttribute("guid"))
                            {
                                ComponentDescription description = ComponentHelper.FindDescription(new Guid(element.Attributes["guid"].InnerText));
                                if (description != null)
                                {
                                    Toolbox.ToolboxItem newItem = new Toolbox.ToolboxItem();
                                    newItem.Tag = "@rid:" + description.RuntimeID;
                                    newItem.ToolTip = description.ComponentName;
                                    TextBlock contentBlock = new TextBlock();
                                    contentBlock.Text = description.ComponentName;
                                    contentBlock.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                                    contentBlock.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                                    newItem.Content = contentBlock;
                                    if (description.Metadata.Icon != null)
                                    {
                                        Canvas contentCanvas = new Canvas();
                                        contentCanvas.Width = 45;
                                        contentCanvas.Height = 45;
                                        var newImage = new Image() { Width = 45, Height = 45, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = System.Windows.VerticalAlignment.Center, Source = description.Metadata.Icon as ImageSource };
                                        newImage.Effect = new System.Windows.Media.Effects.DropShadowEffect();
                                        newImage.SetValue(System.Windows.Media.RenderOptions.BitmapScalingModeProperty, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                                        contentCanvas.Children.Add(newImage);
                                        newItem.Content = contentCanvas;
                                    }
                                    newItem.Click += new RoutedEventHandler(toolboxButton_Click);
                                    newCategory.Items.Add(newItem);

                                    // Shortcut
                                    if (element.HasAttribute("key") && KeyTextConverter.IsValidLetterKey(element.Attributes["key"].InnerText))
                                    {
                                        Key key = (Key)Enum.Parse(typeof(Key), element.Attributes["key"].InnerText);

                                        if (!m_toolboxShortcuts.ContainsKey(key))
                                            m_toolboxShortcuts.Add(key, "@rid:" + description.RuntimeID);
                                    }
                                }
                            }
                            else if (element.HasAttribute("type") && element.HasAttribute("configuration"))
                            {
                                ComponentDescription description = ComponentHelper.FindDescription(element.Attributes["type"].InnerText);
                                if (description != null)
                                {
                                    ComponentConfiguration configuration = description.Metadata.Configurations.FirstOrDefault(configItem => configItem.Name == element.Attributes["configuration"].InnerText);
                                    if (configuration != null)
                                    {
                                        Toolbox.ToolboxItem newItem = new Toolbox.ToolboxItem();
                                        newItem.Tag = "@rid:" + description.RuntimeID + ", @config: " + configuration.Name;
                                        newItem.ToolTip = configuration.Name;
                                        TextBlock contentBlock = new TextBlock();
                                        contentBlock.Text = configuration.Name;
                                        contentBlock.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                                        contentBlock.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                                        newItem.Content = contentBlock;
                                        if (configuration.Icon != null)
                                        {
                                            Canvas contentCanvas = new Canvas();
                                            contentCanvas.Width = 45;
                                            contentCanvas.Height = 45;
                                            var newImage = new Image() { Width = 45, Height = 45, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = System.Windows.VerticalAlignment.Center, Source = configuration.Icon as ImageSource };
                                            newImage.Effect = new System.Windows.Media.Effects.DropShadowEffect();
                                            newImage.SetValue(System.Windows.Media.RenderOptions.BitmapScalingModeProperty, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                                            contentCanvas.Children.Add(newImage);
                                            newItem.Content = contentCanvas;
                                        }
                                        else if (description.Metadata.Icon != null)
                                        {
                                            Canvas contentCanvas = new Canvas();
                                            contentCanvas.Width = 45;
                                            contentCanvas.Height = 45;
                                            var newImage = new Image() { Width = 45, Height = 45, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = System.Windows.VerticalAlignment.Center, Source = description.Metadata.Icon as ImageSource };
                                            newImage.Effect = new System.Windows.Media.Effects.DropShadowEffect();
                                            newImage.SetValue(System.Windows.Media.RenderOptions.BitmapScalingModeProperty, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                                            contentCanvas.Children.Add(newImage);
                                            newItem.Content = contentCanvas;
                                        }
                                        newItem.Click += new RoutedEventHandler(toolboxButton_Click);
                                        newCategory.Items.Add(newItem);

                                        // Shortcut
                                        if (element.HasAttribute("key") && KeyTextConverter.IsValidLetterKey(element.Attributes["key"].InnerText))
                                        {
                                            Key key = (Key)Enum.Parse(typeof(Key), element.Attributes["key"].InnerText);

                                            if (!m_toolboxShortcuts.ContainsKey(key))
                                                m_toolboxShortcuts.Add(key, "@rid:" + description.RuntimeID + ", @config: " + configuration.Name);
                                        }
                                    }
                                }
                            }
                            else if (element.HasAttribute("type"))
                            {
                                ComponentDescription description = ComponentHelper.FindDescription(element.Attributes["type"].InnerText);
                                if (description != null)
                                {
                                    Toolbox.ToolboxItem newItem = new Toolbox.ToolboxItem();
                                    newItem.Tag = "@rid:" + description.RuntimeID;
                                    newItem.ToolTip = description.ComponentName;
                                    TextBlock contentBlock = new TextBlock();
                                    contentBlock.Text = description.ComponentName;
                                    contentBlock.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                                    contentBlock.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                                    newItem.Content = contentBlock;
                                    if (description.Metadata.Icon != null)
                                    {
                                        Canvas contentCanvas = new Canvas();
                                        contentCanvas.Width = 45;
                                        contentCanvas.Height = 45;
                                        var newImage = new Image() { Width = 45, Height = 45, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = System.Windows.VerticalAlignment.Center, Source = description.Metadata.Icon as ImageSource };
                                        newImage.Effect = new System.Windows.Media.Effects.DropShadowEffect();
                                        newImage.SetValue(System.Windows.Media.RenderOptions.BitmapScalingModeProperty, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                                        contentCanvas.Children.Add(newImage);
                                        newItem.Content = contentCanvas;
                                    }
                                    newItem.Click += new RoutedEventHandler(toolboxButton_Click);
                                    newCategory.Items.Add(newItem);

                                    // Shortcut
                                    if (element.HasAttribute("key") && KeyTextConverter.IsValidLetterKey(element.Attributes["key"].InnerText))
                                    {
                                        Key key = (Key)Enum.Parse(typeof(Key), element.Attributes["key"].InnerText);

                                        if (!m_toolboxShortcuts.ContainsKey(key))
                                            m_toolboxShortcuts.Add(key, "@rid:" + description.RuntimeID);
                                    }
                                }
                            }
                        }
                    }
                    if (newCategory.Items.Count > 0)
                        mainToolbox.Items.Add(newCategory);
                }
            }
            catch (Exception)
            {
                MessageBox.Show("The toolbox is corrupt. Please go to Tools->Toolbox to add items.", "Toolbox Corrupt", MessageBoxButton.OK, MessageBoxImage.Error);
#if PORTABLE
                File.WriteAllText(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\settings\\toolbox.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><display></display>");
#else
                File.WriteAllText(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Circuit Diagram\\toolbox.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><display></display>");
#endif
            }
        }

        void toolboxButton_Click(object sender, RoutedEventArgs e)
        {
            circuitDisplay.NewComponentData = (sender as Toolbox.ToolboxItem).Tag as string;
        }

        private void circuitDisplay_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            gridEditor.Children.Clear();
            if (e.AddedItems.Count > 0 && (e.AddedItems[0] as Component).Editor != null)
                gridEditor.Children.Add((e.AddedItems[0] as Component).Editor as ComponentEditor);
        }

        private void btnSelect_Click(object sender, RoutedEventArgs e)
        {
            if (!this.IsInitialized)
                return;
            circuitDisplay.NewComponentData = null;
        }

        CircuitDiagram.IO.CDDX.CDDXSaveOptions m_defaultSaveOptions;
        CircuitDiagram.IO.CDDX.CDDXSaveOptions m_lastSaveOptions;
        private void LoadDefaultCDDXSaveOptions()
        {
            string settingsData = Settings.Settings.Read("DefaultCDDXSaveSettings") as string;
            if (settingsData != null)
            {
                using (MemoryStream stream = new MemoryStream(System.Convert.FromBase64String(settingsData)))
                {
                    BinaryFormatter binaryFormatter = new BinaryFormatter();
                    m_defaultSaveOptions = (IO.CDDX.CDDXSaveOptions)binaryFormatter.Deserialize(stream);
                }
            }
            else
                m_defaultSaveOptions = new IO.CDDX.CDDXSaveOptions();
        }

        void SetStatusText(string text)
        {
            lblStatus.Text = text;
            m_statusTimer.Start();
        }

        private void CheckForUpdates(bool notifyIfNoUpdate)
        {
            // Check for new version
            System.Reflection.Assembly _assemblyInfo = System.Reflection.Assembly.GetExecutingAssembly();
            Version thisVersion = _assemblyInfo.GetName().Version;
            BuildChannelAttribute channelAttribute = _assemblyInfo.GetCustomAttributes(typeof(BuildChannelAttribute), false).FirstOrDefault(item => item is BuildChannelAttribute) as BuildChannelAttribute;
            if (channelAttribute == null)
                channelAttribute = new BuildChannelAttribute("", BuildChannelAttribute.ChannelType.Stable, 0);

            try
            {
                System.Net.WebRequest updateDocStream = System.Net.WebRequest.Create("http://www.circuit-diagram.org/app/appversion.xml");
                XmlDocument updateDoc = new XmlDocument();
                updateDoc.Load(updateDocStream.GetResponse().GetResponseStream());

                bool foundUpdate = false;
                if (channelAttribute.Type == BuildChannelAttribute.ChannelType.Dev)
                {
                    // Check for latest dev build
                    XmlNode devChannel = updateDoc.SelectSingleNode("/version/application[@name='CircuitDiagram']/channel[@name='Dev']");
                    if (devChannel != null)
                    {
                        Version serverVersion = null;
                        string serverVersionName = null;
                        int serverIncrement = 0;
                        string serverDownloadUrl = null;

                        foreach (XmlNode childNode in devChannel.ChildNodes)
                        {
                            switch (childNode.Name)
                            {
                                case "version":
                                    serverVersion = new Version(childNode.InnerText);
                                    break;
                                case "name":
                                    serverVersionName = childNode.InnerText;
                                    break;
                                case "increment":
                                    serverIncrement = int.Parse(childNode.InnerText);
                                    break;
                                case "url":
                                    serverDownloadUrl = childNode.InnerText;
                                    break;
                            }
                        }

                        if (serverVersion != null && thisVersion.CompareTo(serverVersion) < 0 || (thisVersion.CompareTo(serverVersion) == 0 && channelAttribute.Increment < serverIncrement))
                        {
                            this.Dispatcher.Invoke(new Action(() => winNewVersion.Show(this, NewVersionWindowType.NewVersionAvailable, serverVersionName, serverDownloadUrl)));
                            foundUpdate = true;
                        }
                    }
                }

                if (!foundUpdate)
                {
                    // Check for latest stable build
                    XmlNode stableChannel = updateDoc.SelectSingleNode("/version/application[@name='CircuitDiagram']/channel[@name='Stable']");
                    if (stableChannel != null)
                    {
                        Version serverVersion = null;
                        string serverVersionName = null;
                        string serverDownloadUrl = null;

                        foreach (XmlNode childNode in stableChannel.ChildNodes)
                        {
                            switch (childNode.Name)
                            {
                                case "version":
                                    serverVersion = new Version(childNode.InnerText);
                                    break;
                                case "name":
                                    serverVersionName = childNode.InnerText;
                                    break;
                                case "url":
                                    serverDownloadUrl = childNode.InnerText;
                                    break;
                            }
                        }

                        if (serverVersion != null && thisVersion.CompareTo(serverVersion) < 0)
                        {
                            this.Dispatcher.Invoke(new Action(() => winNewVersion.Show(this, NewVersionWindowType.NewVersionAvailable, serverVersionName, serverDownloadUrl)));
                            foundUpdate = true;
                        }
                    }
                }

                if (!foundUpdate && notifyIfNoUpdate)
                    this.Dispatcher.Invoke(new Action(() => winNewVersion.Show(this, NewVersionWindowType.NoNewVersionAvailable, null, null)));
            }
            catch (Exception)
            {
                if (notifyIfNoUpdate)
                    this.Dispatcher.Invoke(new Action(() => winNewVersion.Show(this, NewVersionWindowType.Error, null, "http://www.circuit-diagram.org/")));
            }

            Settings.Settings.Write("LastCheckForUpdates", DateTime.Now);
            Settings.Settings.Save();
        }

        void Editor_ComponentUpdated(object sender, ComponentUpdatedEventArgs e)
        {
            UndoAction undoAction = new UndoAction(UndoCommand.ModifyComponents, "edit", new Component[] { e.Component });
            Dictionary<Component, string> previousData = new Dictionary<Component, string>(1);
            previousData.Add(e.Component, e.PreviousData);
            undoAction.AddData("before", previousData);
            Dictionary<Component, string> newData = new Dictionary<Component, string>(1);
            newData.Add(e.Component, e.Component.SerializeToString());
            undoAction.AddData("after", newData);
            UndoManager.AddAction(undoAction);

            // Update connections
            e.Component.ResetConnections();
            e.Component.ApplyConnections(circuitDisplay.Document);
            circuitDisplay.DrawConnections();
        }

        private void lblSliderZoom_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            sliderZoom.Value = 50;
        }

        private void sliderZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (circuitDisplay != null)
                circuitDisplay.LayoutTransform = new ScaleTransform(e.NewValue / 50, e.NewValue / 50);
        }

        #region Menu Bar
        private void mnuFileExport_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog sfd = new Microsoft.Win32.SaveFileDialog();
            sfd.Title = "Export";
            sfd.Filter = "PNG (*.png)|*.png|Scalable Vector Graphics (*.svg)|*.svg"; //"PNG (*.png)|*.png|Scalable Vector Graphics (*.svg)|*.svg|Enhanced Metafile (*.emf)|*.emf";
            sfd.InitialDirectory = Environment.SpecialFolder.MyDocuments.ToString();
            if (sfd.ShowDialog() == true)
            {
                string extension = System.IO.Path.GetExtension(sfd.FileName);
                if (extension == ".svg")
                {
                    SVGRenderer renderer = new SVGRenderer();
                    renderer.Begin();
                    circuitDisplay.Document.Render(renderer);
                    renderer.End();
                    System.IO.File.WriteAllBytes(sfd.FileName, renderer.SVGDocument.ToArray());
                }
                else if (extension == ".png")
                {
                    winExportPNG exportPNGWindow = new winExportPNG();
                    exportPNGWindow.Owner = this;
                    exportPNGWindow.OriginalWidth = circuitDisplay.Width;
                    exportPNGWindow.OriginalHeight = circuitDisplay.Height;
                    exportPNGWindow.Update();
                    if (exportPNGWindow.ShowDialog() == true)
                    {
                        WPFRenderer renderer = new WPFRenderer();
                        renderer.Begin();
                        circuitDisplay.Document.Render(renderer);
                        renderer.End();
                        using (var memoryStream = renderer.GetPNGImage2(exportPNGWindow.OutputWidth, exportPNGWindow.OutputHeight, circuitDisplay.Document.Size.Width, circuitDisplay.Document.Size.Height, exportPNGWindow.OutputBackgroundColour == "White"))
                        {
                            FileStream fileStream = new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write, FileShare.Read);
                            memoryStream.WriteTo(fileStream);
                            fileStream.Close();
                        }
                    }
                }
            }
        }

        private void mnuFileComponents_Click(object sender, RoutedEventArgs e)
        {
            winComponents componentsWindow = new winComponents();
            componentsWindow.Components = ComponentHelper.ComponentDescriptions;
            componentsWindow.Owner = this;
            componentsWindow.ShowDialog();
        }

        private void mnuFileSaveAs_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.Title = "Save As";
            sfd.Filter = "Circuit Diagram Document (*.cddx)|*.cddx|XML Files (*.xml)|*.xml";
            if (sfd.ShowDialog() == true)
            {
                string extension = Path.GetExtension(sfd.FileName);

                if (extension == ".cddx")
                {
                    IO.CDDX.CDDXSaveOptions saveOptions = m_lastSaveOptions;

                    bool doSave = true;
                    bool alwaysUseSettings = Settings.Settings.ReadBool("AlwaysUseCDDXSaveSettings");
                    if (alwaysUseSettings && m_defaultSaveOptions != null)
                        saveOptions = m_defaultSaveOptions;
                    if (!alwaysUseSettings || saveOptions == null)
                    {
                        winCDDXSave saveOptionsDialog = new winCDDXSave();
                        saveOptionsDialog.Owner = this;
                        saveOptionsDialog.SaveOptions = m_defaultSaveOptions;

                        List<ComponentDescription> usedDescriptions = new List<ComponentDescription>();
                        foreach (Component component in circuitDisplay.Document.Components)
                            if (!usedDescriptions.Contains(component.Description))
                                usedDescriptions.Add(component.Description);
                        saveOptionsDialog.AvailableComponents = usedDescriptions;

                        if (saveOptionsDialog.ShowDialog() == true)
                        {
                            doSave = true;
                            saveOptions = saveOptionsDialog.SaveOptions;
                            if (saveOptionsDialog.AlwaysUseSettings)
                            {
                                m_defaultSaveOptions = saveOptionsDialog.SaveOptions;
                                Settings.Settings.Write("AlwaysUseCDDXSaveSettings", true);

                                using (MemoryStream stream = new MemoryStream())
                                {
                                    BinaryFormatter binaryFormatter = new BinaryFormatter();
                                    binaryFormatter.Serialize(stream, m_defaultSaveOptions);

                                    string encodedData = System.Convert.ToBase64String(stream.ToArray());
                                    Settings.Settings.Write("DefaultCDDXSaveSettings", encodedData);
                                }
                            }
                        }
                        else
                            doSave = false;
                    }

                    if (doSave)
                    {
                        m_lastSaveOptions = saveOptions;
                        CircuitDiagram.IO.CDDX.CDDXIO.Write(new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), circuitDisplay.Document, saveOptions);
                        m_docPath = sfd.FileName;
                        m_documentTitle = System.IO.Path.GetFileNameWithoutExtension(sfd.FileName);
                        this.Title = m_documentTitle + " - Circuit Diagram";
                        m_undoManager.SetSaveIndex();
                    }
                }
                else
                {
                    // Save in XML format
                    CircuitDiagram.IO.CircuitDocumentWriter.WriteXml(circuitDisplay.Document, new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                    m_docPath = sfd.FileName;
                    m_documentTitle = System.IO.Path.GetFileNameWithoutExtension(sfd.FileName);
                    this.Title = m_documentTitle + " - Circuit Diagram";
                    m_undoManager.SetSaveIndex();
                }
            }
        }

        private void mnuToolsToolbox_Click(object sender, RoutedEventArgs e)
        {
            winToolbox toolboxWindow = new winToolbox();
            toolboxWindow.Owner = this;
            if (toolboxWindow.ShowDialog() == true)
            {
                LoadToolbox();
            }
        }

        private void mnuRecentItem_Click(object sender, RoutedEventArgs e)
        {
            // Load the clicked item
            if ((e.OriginalSource as MenuItem).Header is string && System.IO.File.Exists((e.OriginalSource as MenuItem).Header as string))
                OpenDocument((e.OriginalSource as MenuItem).Header as string);
        }

        private void mnuFileOptions_Click(object sender, RoutedEventArgs e)
        {
            winOptions options = new winOptions();
            options.Owner = this;
            options.ComponentRepresentations = m_componentRepresentations;
            if (options.ShowDialog() == true)
            {
                CircuitDiagram.Settings.Settings.Save();

                ApplySettings();

                circuitDisplay.DrawConnections();

                // Save implementation representations
#if PORTABLE
                XmlTextWriter writer = new XmlTextWriter(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\settings\\implementations.xml", Encoding.UTF8);
#else
                XmlTextWriter writer = new XmlTextWriter(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Circuit Diagram\\implementations.xml", Encoding.UTF8);
#endif
                writer.Formatting = Formatting.Indented;
                writer.WriteStartDocument();
                writer.WriteStartElement("implementations");
                foreach (var source in m_componentRepresentations)
                {
                    if (source.Items.Count == 0)
                        continue;

                    writer.WriteStartElement("source");

                    writer.WriteAttributeString("definitions", source.ImplementationSet);

                    foreach (var item in source.Items)
                    {
                        writer.WriteStartElement("add");

                        writer.WriteAttributeString("item", item.ImplementationName);
                        writer.WriteStartElement("guid");
                        writer.WriteValue(item.ToGUID.ToString());
                        writer.WriteEndElement();
                        if (!String.IsNullOrEmpty(item.ToConfiguration))
                        {
                            writer.WriteStartElement("configuration");
                            writer.WriteValue(item.ToConfiguration);
                            writer.WriteEndElement();
                        }

                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }
                writer.Flush();
                writer.Close();
            }
        }

        private void mnuFileDocument_Click(object sender, RoutedEventArgs e)
        {
            winDocumentProperties documentInfoWindow = new winDocumentProperties();
            documentInfoWindow.Owner = this;
            documentInfoWindow.SetDocument(circuitDisplay.Document);
            documentInfoWindow.ShowDialog();
        }

        private void mnuHelpDocumentation_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("http://www.circuit-diagram.org/help");
        }

        private void mnuCheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckForUpdates(true);
        }

        private void mnuHelpAbout_Click(object sender, RoutedEventArgs e)
        {
            winAbout aboutWindow = new winAbout();
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }

        private void mnuFileExit_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void mnuEditResizeDocument(object sender, RoutedEventArgs e)
        {
            winDocumentSize docSizeWindow = new winDocumentSize();
            docSizeWindow.Owner = this;
            docSizeWindow.DocumentWidth = circuitDisplay.Document.Size.Width;
            docSizeWindow.DocumentHeight = circuitDisplay.Document.Size.Height;
            if (docSizeWindow.ShowDialog() == true)
            {
                circuitDisplay.Document.Size = new Size(docSizeWindow.DocumentWidth, docSizeWindow.DocumentHeight);
                circuitDisplay.DocumentSizeChanged();
            }
        }

        private void mnuFilePrint_Click(object sender, RoutedEventArgs e)
        {
            PrintDialog pDlg = new PrintDialog();
            if (pDlg.ShowDialog() == true)
            {
                WPFRenderer renderer = new WPFRenderer();
                renderer.Begin();
                circuitDisplay.Document.Render(renderer);
                renderer.End();
                FixedDocument document = renderer.GetDocument(new Size(pDlg.PrintableAreaWidth, pDlg.PrintableAreaHeight));
                pDlg.PrintDocument(document.DocumentPaginator, m_documentTitle + " - Circuit Diagram");
            }
        }
        #endregion

        #region RecentFiles
        private void AddRecentFile(string path)
        {
            if (RecentFiles.Count == 1 && RecentFiles[0] == "(empty)")
                RecentFiles.Clear();
            if (RecentFiles.Contains(path))
                RecentFiles.Remove(path);
            RecentFiles.Insert(0, path);
            if (RecentFiles.Count > 10)
                RecentFiles.RemoveAt(10);
        }

        private void LoadRecentFiles()
        {
            string[] files = CircuitDiagram.Settings.Settings.Read("recentfiles") as string[];
            if (files == null)
            {
                RecentFiles.Add("(empty)");
                return;
            }
            foreach (string file in files)
                RecentFiles.Add(file);
        }

        private void SaveRecentFiles()
        {
            if (RecentFiles.Count == 1 && RecentFiles[0] == "(empty)")
                return;
            CircuitDiagram.Settings.Settings.Write("recentfiles", RecentFiles.ToArray());
            CircuitDiagram.Settings.Settings.Save();
        }
        #endregion

        #region Commands
        private void CommandNew_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private void CommandNew_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            winNewDocument newDocumentWindow = new winNewDocument();
            newDocumentWindow.Owner = this;
            if (newDocumentWindow.ShowDialog() == true)
            {
                CircuitDocument newDocument = new CircuitDocument();
                newDocument.Size = new Size(newDocumentWindow.DocumentWidth, newDocumentWindow.DocumentHeight);
                circuitDisplay.Document = newDocument;
                circuitDisplay.DrawConnections();
                this.Title = "Untitled - Circuit Diagram";
                m_documentTitle = "Untitled";
                m_undoManager = new UndoManager();
                circuitDisplay.UndoManager = m_undoManager;
                m_undoManager.ActionDelegate = new CircuitDiagram.UndoManager.UndoActionDelegate(UndoActionProcessor);
                m_undoManager.ActionOccurred += new EventHandler(m_undoManager_ActionOccurred);
            }
        }

        private void CommandOpen_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private void CommandOpen_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Title = "Open";
            ofd.Filter = "Supported Circuits (*.cddx;*.xml)|*.cddx;*.xml|Circuit Diagram Document (*.cddx)|*.cddx|XML Files (*.xml)|*.xml|All Files (*.*)|*.*";
            ofd.InitialDirectory = Environment.SpecialFolder.MyDocuments.ToString();
            if (ofd.ShowDialog() == true)
            {
                OpenDocument(ofd.FileName);
            }
        }

        private void CommandSave_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private void CommandSave_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (m_docPath != "")
            {
                string extension = Path.GetExtension(m_docPath);
                if (extension == ".cddx")
                {
                    // Save in CDDX format
                    if (m_lastSaveOptions == null)
                        m_lastSaveOptions = m_defaultSaveOptions;
                    CircuitDiagram.IO.CDDX.CDDXIO.Write(new FileStream(m_docPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), circuitDisplay.Document, m_lastSaveOptions);
                }
                else
                {
                    // Save in XML format
                    CircuitDiagram.IO.CircuitDocumentWriter.WriteXml(circuitDisplay.Document, new FileStream(m_docPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                }
                this.Title = System.IO.Path.GetFileNameWithoutExtension(m_docPath) + " - Circuit Diagram";
                UndoManager.SetSaveIndex();
            }
            else
                mnuFileSaveAs_Click(sender, e);
        }

        private void CommandUndo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = UndoManager.CanStepBackwards();
        }

        private void CommandUndo_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            UndoManager.StepBackwards();
        }

        private void CommandRedo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = UndoManager.CanStepForwards();
        }

        private void CommandRedo_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            UndoManager.StepForwards();
        }

        private void DeleteComponentCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            circuitDisplay.DeleteComponentCommand_CanExecute(sender, e);
        }

        private void DeleteComponentCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            circuitDisplay.DeleteComponentCommand(sender, e);
        }

        private void FlipComponentCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            circuitDisplay.FlipComponentCommand_CanExecute(sender, e);
        }

        private void FlipComponentCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            circuitDisplay.FlipComponentCommand(sender, e);
        }

        private void RotateComponentCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            circuitDisplay.RotateComponentCommand_CanExecute(sender, e);
        }

        private void RotateComponentCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            circuitDisplay.RotateComponentCommand_Executed(sender, e);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if ((circuitDisplay.IsMouseOver || mainToolbox.IsMouseOver) && !e.IsRepeat && e.IsDown)
            {
                // Check component shortcuts
                if (e.Key == Key.V) // Move/select
                {
                    circuitDisplay.NewComponentData = null;

                    SetStatusText("Select tool.");

                    e.Handled = true;
                }
                else if (e.Key == Key.W) // Wire
                {
                    circuitDisplay.NewComponentData = "@rid: " + ComponentHelper.WireDescription.RuntimeID;

                    SetStatusText("Placing wire.");

                    e.Handled = true;
                }
                else if (e.Key == Key.Delete)
                {
                    circuitDisplay.DeleteComponentCommand(this, null);

                    e.Handled = true;
                }
                else
                {
                    // Check custom toolbox entries
                    if (m_toolboxShortcuts.ContainsKey(e.Key))
                    {
                        circuitDisplay.NewComponentData = m_toolboxShortcuts[e.Key];

                        string rid = circuitDisplay.NewComponentData.Substring(circuitDisplay.NewComponentData.IndexOf("@rid:") + 5);
                        string configuration = null;
                        if (rid.Contains(" "))
                        {
                            configuration = rid.Substring(rid.IndexOf("@config:") + 8);
                            rid = rid.Substring(0, rid.IndexOf(" "));
                        }

                        ComponentDescription description = ComponentHelper.FindDescriptionByRuntimeID(int.Parse(rid));

                        // TODO: add configuration to status text

                        if (description != null)
                            SetStatusText(String.Format("Placing component: {0}", description.ComponentName));

                        e.Handled = true;
                    }
                }
            }
        }
        #endregion

        #region Undo Manager
        void m_undoManager_ActionOccurred(object sender, EventArgs e)
        {
            if (UndoManager.IsSavedState())
            {
                this.Title = m_documentTitle + " - Circuit Diagram";
            }
            else
            {
                this.Title = m_documentTitle + "* - Circuit Diagram";
            }
        }

        void UndoActionProcessor(object sender, UndoActionEventArgs e)
        {
            if (e.Event == UndoActionEvent.Remove)
            {
                switch (e.Action.Command)
                {
                    case UndoCommand.ModifyComponents:
                        {
                            Component[] components = (e.Action.GetDefaultData() as Component[]);
                            Dictionary<Component, string> beforeData = e.Action.GetData<Dictionary<Component, string>>("before");
                            foreach (Component component in components)
                            {
                                component.Deserialize(ComponentDataString.ConvertToDictionary(beforeData[component]));
                                component.InvalidateVisual();
                                circuitDisplay.RedrawComponent(component);
                                component.ResetConnections();
                                component.ApplyConnections(circuitDisplay.Document);
                            }
                        }
                        break;
                    case UndoCommand.DeleteComponents:
                        {
                            Component[] components = (e.Action.GetDefaultData() as Component[]);
                            foreach (Component component in components)
                            {
                                circuitDisplay.Document.Elements.Add(component);
                                component.ResetConnections();
                                component.ApplyConnections(circuitDisplay.Document);
                            }
                        }
                        break;
                    case UndoCommand.AddComponent:
                        {
                            Component component = (e.Action.GetDefaultData() as Component);
                            component.DisconnectConnections();
                            circuitDisplay.Document.Elements.Remove(component);
                        }
                        break;
                }
            }
            else
            {
                switch (e.Action.Command)
                {
                    case UndoCommand.ModifyComponents:
                        {
                            Component[] components = (e.Action.GetDefaultData() as Component[]);
                            Dictionary<Component, string> beforeData = e.Action.GetData<Dictionary<Component, string>>("after");
                            foreach (Component component in components)
                            {
                                component.Deserialize(ComponentDataString.ConvertToDictionary(beforeData[component]));
                                component.InvalidateVisual();
                                circuitDisplay.RedrawComponent(component);
                                component.ResetConnections();
                                component.ApplyConnections(circuitDisplay.Document);
                            }
                        }
                        break;
                    case UndoCommand.DeleteComponents:
                        {
                            Component[] components = (e.Action.GetDefaultData() as Component[]);
                            foreach (Component component in components)
                            {
                                component.DisconnectConnections();
                                circuitDisplay.Document.Elements.Remove(component);
                            }
                        }
                        break;
                    case UndoCommand.AddComponent:
                        {
                            Component component = (e.Action.GetDefaultData() as Component);
                            circuitDisplay.Document.Elements.Add(component);
                            component.ResetConnections();
                            component.ApplyConnections(circuitDisplay.Document);
                        }
                        break;
                }
            }

            circuitDisplay.DrawConnections();
        }
        #endregion
    }
}