namespace MdCompile
{
    class CodeBlock
    {
        public int StartLineIndex { get; set; }

        public int LineCount { get; set; }

        public string Language { get; set; }

        public CodeBlockMetadata Metadata { get; set; }
    }
}