using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpubFixServiceFixture : TestBase
    {
        [Test]
        public void command_should_be_constructible()
        {
            var cmd = new EpubFixCommand();
            cmd.AuthorId.Should().BeNull();
            cmd.BookId.Should().BeNull();
            cmd.SendUpdatesToClient.Should().BeTrue();
            cmd.IsTypeExclusive.Should().BeFalse();
            cmd.UpdateScheduledTask.Should().BeFalse();
        }
    }
}
