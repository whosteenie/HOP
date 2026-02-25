namespace KINEMATION.KAnimationCore.Editor.Tools
{
    public interface IEditorTool
    {
        public void Init();
        public void Render();
        public string GetToolCategory();
        public string GetToolName();
        public string GetDocsURL();
        public string GetToolDescription();
    }
}