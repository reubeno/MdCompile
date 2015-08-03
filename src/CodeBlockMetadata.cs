using System.Collections.Generic;
using NClap.Metadata;

namespace MdCompile
{
    [ArgumentSet(PublicMembersAreNamedArguments = true)]
    class CodeBlockMetadata
    {
        [NamedArgument(DefaultValue = true)]
        public bool Compile { get; set; } = true;

        [NamedArgument(LongName = "assembly")]
        public string AssemblyId { get; set; }

        [NamedArgument(ArgumentFlags.Multiple, LongName = "import")]
        public List<string> Imports { get; set; } = new List<string>();

        [NamedArgument(DefaultValue = true)]
        public bool WrapInNamespace { get; set; } = true;

        [NamedArgument(DefaultValue = false)]
        public bool WrapInClass { get; set; } = false;

        public string Prefix { get; set; }

        public string Suffix { get; set; }
    }
}
