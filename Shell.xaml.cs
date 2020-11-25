using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Comlink.WebView2;
using Fyn.Windows.Service;
using IdentityModel.OidcClient;

namespace Fyn.Windows
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class Shell : Window
    {
        public LoginResult? Login { get; private set; }

        public Shell()
        {
            InitializeComponent();
            Initialize();
        }

        private async void Initialize()
        {
            await AppFrame.EnsureCoreWebView2Async();

            Login = await Authentication.Signin("https://unifyned.cloud");
        }
    }
}
