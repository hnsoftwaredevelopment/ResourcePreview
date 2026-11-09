using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ResourcePreview.Preview;

namespace ResourcePreview.Tests
{
    /// <summary>The live sample with the mouse over it and pressed: trigger setters applied without a mouse.</summary>
    [TestClass]
    public class StateSimulatorTests
    {
        private static T Parse<T>(string body) => (T)((ResourceDictionary)XamlReader.Parse(Xaml.Dictionary(body)))["T"];

        private static IEnumerable<DependencyObject> All(DependencyObject root)
        {
            yield return root;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject d)
                {
                    foreach (DependencyObject descendant in All(d))
                    {
                        yield return descendant;
                    }
                }
            }
        }

        private static string[] Labels(UIElement root) => All(root).OfType<TextBlock>().Select(t => t.Text).ToArray();

        private const string ChromeTemplate =
            "<ControlTemplate x:Key=\"T\" TargetType=\"Button\">" +
            "<Border x:Name=\"Chrome\" Background=\"White\"><ContentPresenter /></Border>" +
            "<ControlTemplate.Triggers>{0}</ControlTemplate.Triggers></ControlTemplate>";

        [TestMethod]
        public void TemplateTrigger_WithTargetName_SetsThePart()
        {
            Sta.Run(() =>
            {
                var template = Parse<ControlTemplate>(string.Format(ChromeTemplate,
                    "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"Chrome\" Property=\"Background\" Value=\"Red\" /></Trigger>"));
                Button[] buttons = All(LiveSampleRenderer.ForTemplate(template).Sample).OfType<Button>().ToArray();

                Assert.AreEqual(2, buttons.Length);
                Assert.AreEqual(Colors.White, ((SolidColorBrush)((Border)template.FindName("Chrome", buttons[0])).Background).Color);
                Assert.AreEqual(Colors.Red, ((SolidColorBrush)((Border)template.FindName("Chrome", buttons[1])).Background).Color);
                CollectionAssert.AreEqual(new[] { "Normal", "Mouse over" }, Labels(LiveSampleRenderer.ForTemplate(template).Sample));
            });
        }

        [TestMethod]
        public void Pressed_AlsoAppliesMouseOver_LaterTriggerWins()
        {
            Sta.Run(() =>
            {
                var style = Parse<Style>(
                    "<Style x:Key=\"T\" TargetType=\"Button\"><Style.Triggers>" +
                    "<Trigger Property=\"IsPressed\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.5\" /></Trigger>" +
                    "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.8\" /><Setter Property=\"FontWeight\" Value=\"Bold\" /></Trigger>" +
                    "</Style.Triggers></Style>");
                Button[] buttons = All(LiveSampleRenderer.ForStyle(style).Sample).OfType<Button>().ToArray();

                // In WPF the trigger declared last wins, so a pressed button gets 0.8 here.
                CollectionAssert.AreEqual(new[] { 1.0, 0.8, 0.8 }, buttons.Select(b => b.Opacity).ToArray());
                Assert.AreEqual(FontWeights.Bold, buttons[2].FontWeight);
            });
        }

        [TestMethod]
        public void MultiTrigger_OtherConditionFalse_IsNotApplied()
        {
            Sta.Run(() =>
            {
                var style = Parse<Style>(
                    "<Style x:Key=\"T\" TargetType=\"Button\"><Style.Triggers>" +
                    "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.8\" /></Trigger>" +
                    "<MultiTrigger><MultiTrigger.Conditions><Condition Property=\"IsMouseOver\" Value=\"True\" /><Condition Property=\"IsFocused\" Value=\"True\" /></MultiTrigger.Conditions>" +
                    "<Setter Property=\"FontWeight\" Value=\"Bold\" /></MultiTrigger>" +
                    "<MultiTrigger><MultiTrigger.Conditions><Condition Property=\"IsMouseOver\" Value=\"True\" /><Condition Property=\"IsEnabled\" Value=\"True\" /></MultiTrigger.Conditions>" +
                    "<Setter Property=\"FontStyle\" Value=\"Italic\" /></MultiTrigger>" +
                    "</Style.Triggers></Style>");
                Button hover = All(LiveSampleRenderer.ForStyle(style).Sample).OfType<Button>().ElementAt(1);

                Assert.AreEqual(FontWeights.Normal, hover.FontWeight, "IsFocused is false");
                Assert.AreEqual(FontStyles.Italic, hover.FontStyle, "IsEnabled is true");
            });
        }

        [TestMethod]
        public void StyleTriggersWinOverTemplateTriggers()
        {
            Sta.Run(() =>
            {
                var style = Parse<Style>(
                    "<Style x:Key=\"T\" TargetType=\"Button\">" +
                    "<Setter Property=\"Template\"><Setter.Value><ControlTemplate TargetType=\"Button\"><ContentPresenter />" +
                    "<ControlTemplate.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.3\" /></Trigger></ControlTemplate.Triggers>" +
                    "</ControlTemplate></Setter.Value></Setter>" +
                    "<Style.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.7\" /></Trigger></Style.Triggers></Style>");

                Assert.AreEqual(0.7, All(LiveSampleRenderer.ForStyle(style).Sample).OfType<Button>().ElementAt(1).Opacity);
            });
        }

        [TestMethod]
        public void BasedOn_TriggersAreFollowed()
        {
            Sta.Run(() =>
            {
                string xaml = Xaml.Dictionary(
                    "<Style x:Key=\"Base\" TargetType=\"Button\"><Style.Triggers><Trigger Property=\"IsPressed\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.4\" /></Trigger></Style.Triggers></Style>" +
                    "<Style x:Key=\"T\" TargetType=\"Button\" BasedOn=\"{StaticResource Base}\" />");
                var style = (Style)((ResourceDictionary)XamlReader.Parse(xaml))["T"];

                CollectionAssert.AreEqual(new[] { "Normal", "Pressed" }, Labels(LiveSampleRenderer.ForStyle(style).Sample));
            });
        }

        [TestMethod]
        public void DataTrigger_OnSelfIsMouseOver_Counts()
        {
            Sta.Run(() =>
            {
                var style = Parse<Style>(
                    "<Style x:Key=\"T\" TargetType=\"Button\"><Style.Triggers>" +
                    "<DataTrigger Binding=\"{Binding IsMouseOver, RelativeSource={RelativeSource Self}}\" Value=\"True\"><Setter Property=\"Opacity\" Value=\"0.8\" /></DataTrigger>" +
                    "</Style.Triggers></Style>");

                Assert.AreEqual(0.8, All(LiveSampleRenderer.ForStyle(style).Sample).OfType<Button>().ElementAt(1).Opacity);
            });
        }

        [TestMethod]
        public void NoStateTriggers_OneSampleWithoutLabel()
        {
            Sta.Run(() =>
            {
                // IsMouseOver="False" and a trigger with only EnterActions do not change the sample by themselves.
                var style = Parse<Style>(
                    "<Style x:Key=\"T\" TargetType=\"Button\"><Setter Property=\"Opacity\" Value=\"0.9\" /><Style.Triggers>" +
                    "<Trigger Property=\"IsMouseOver\" Value=\"False\"><Setter Property=\"Opacity\" Value=\"0.5\" /></Trigger>" +
                    "<Trigger Property=\"IsPressed\" Value=\"True\"><Trigger.EnterActions><BeginStoryboard><Storyboard /></BeginStoryboard></Trigger.EnterActions></Trigger>" +
                    "</Style.Triggers></Style>");
                UIElement sample = LiveSampleRenderer.ForStyle(style).Sample;

                Assert.AreEqual(1, All(sample).OfType<Button>().Count());
                Assert.AreEqual(0, Labels(sample).Length);
            });
        }

        [TestMethod]
        public void VisualStateManager_MouseOverState_IsShown()
        {
            Sta.Run(() =>
            {
                var template = Parse<ControlTemplate>(
                    "<ControlTemplate x:Key=\"T\" TargetType=\"Button\"><Border x:Name=\"Chrome\" Background=\"White\">" +
                    "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name=\"CommonStates\">" +
                    "<VisualState x:Name=\"Normal\" /><VisualState x:Name=\"MouseOver\"><Storyboard>" +
                    "<ColorAnimation Storyboard.TargetName=\"Chrome\" Storyboard.TargetProperty=\"(Border.Background).(SolidColorBrush.Color)\" To=\"Red\" Duration=\"0\" />" +
                    "</Storyboard></VisualState></VisualStateGroup></VisualStateManager.VisualStateGroups>" +
                    "<ContentPresenter /></Border></ControlTemplate>");

                CollectionAssert.AreEqual(new[] { "Normal", "Mouse over" }, Labels(LiveSampleRenderer.ForTemplate(template).Sample));
            });
        }
    }
}
