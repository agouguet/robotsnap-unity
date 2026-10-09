using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace RobotSNAP.Tests.Editor
{
    /// <summary>
    /// The ROS chip of the application bar.
    ///
    /// It is the bar's one chip that answers a click: it hands the robot the view is on to ROS 2, and hands it
    /// back to the scenario on the next press. An edit-mode tree has no panel to raise a pointer click through,
    /// so - as in <see cref="AppStatusBarQuitTests"/> for the power button - the click path is walked directly,
    /// through <see cref="AppStatusBar.RequestRosControl"/>. The effect the press runs and the reading that
    /// arms the chip are both parameters of the bar, which is what lets these cases run without a scene.
    /// </summary>
    public sealed class AppStatusBarRosControlTests
    {
        private const string MainWindowPath = "Assets/UI/MainWindow.uxml";

        private static VisualElement BuildMainWindow()
        {
            VisualTreeAsset window = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(MainWindowPath);
            Assert.That(window, Is.Not.Null, $"The main window is missing at {MainWindowPath}.");
            return window.Instantiate();
        }

        private static Button RosChip(VisualElement root)
        {
            Button chip = root.Q<Button>("StatusRos");
            Assert.That(chip, Is.Not.Null, "the status bar carries the ROS chip as a button");
            return chip;
        }

        /// <summary>
        /// The chip is a button that names what it does, and its two children survived the change: the dot the
        /// bar lights while the bridge is reachable, and the label that reads "ROS 2".
        /// </summary>
        [Test]
        public void TheRosChipIsAButtonThatSaysWhatItDoes()
        {
            VisualElement root = BuildMainWindow();
            var bar = new AppStatusBar(root, () => { }, () => { }, () => false);

            try
            {
                Button chip = RosChip(root);
                Assert.That(chip.tooltip, Is.Not.Empty, "the chip explains itself on hover");
                Assert.That(chip.tooltip, Does.Contain("ROS"), "the tooltip names the mode the click switches to");
                Assert.That(chip.tooltip, Does.Contain("robot"), "the tooltip names the robot the click acts on");
                Assert.That(chip.ClassListContains("status-chip"), Is.True, "the chip keeps the bar's chip look");
                Assert.That(chip.Q<VisualElement>("StatusRosDot"), Is.Not.Null, "the reachability dot is kept");
                Assert.That(chip.Q<Label>("StatusRosLabel"), Is.Not.Null, "the label is kept");
                Assert.That(chip.Q<Label>("StatusRosLabel").text, Is.EqualTo("ROS 2"));
            }
            finally
            {
                bar.Dispose();
            }
        }

        /// <summary>A press reaches the effect the bar was built with.</summary>
        [Test]
        public void AClickOnTheRosChipRunsTheEffectItWasGiven()
        {
            VisualElement root = BuildMainWindow();
            int toggles = 0;
            var bar = new AppStatusBar(root, () => { }, () => toggles++, () => false);

            try
            {
                Assert.That(toggles, Is.Zero, "the chip does nothing until it is pressed");

                bar.RequestRosControl();

                Assert.That(toggles, Is.EqualTo(1), "the press is what the bar asked for");
            }
            finally
            {
                bar.Dispose();
            }
        }

        /// <summary>
        /// The armed mark follows the reading the bar is given, on the bar's own refresh: off while the target
        /// obeys the scenario, on once ROS drives it, and off again when the robot is handed back.
        /// </summary>
        [Test]
        public void TheRosChipIsArmedOnlyWhileTheTargetObeysRos()
        {
            VisualElement root = BuildMainWindow();
            bool underRos = false;
            var bar = new AppStatusBar(root, () => { }, () => { }, () => underRos);

            try
            {
                Button chip = RosChip(root);
                Assert.That(chip.ClassListContains("is-ros-controlled"), Is.False,
                    "a robot the scenario drives leaves the chip unarmed");

                underRos = true;
                bar.Tick();

                Assert.That(chip.ClassListContains("is-ros-controlled"), Is.True,
                    "a mode switched from outside lights the chip on the bar's own refresh");

                underRos = false;
                bar.Tick();

                Assert.That(chip.ClassListContains("is-ros-controlled"), Is.False,
                    "handing the robot back to the scenario puts the chip out");
            }
            finally
            {
                bar.Dispose();
            }
        }
    }
}
