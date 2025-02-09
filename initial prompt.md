Create an ePub translation pipeline using Microsoft's Semantic Kernel that converts from one language to another one, mostly English to Chinese while preserving their original structure and formatting. The system should:

1. Break down ePub components (content, metadata, structure) into processable chunks
2. Implement a provider-agnostic translation service that can switch between Azure OpenAI and OpenAI
3. Process paragraphs sequentially while maintaining contextual awareness
4. Establish preprocessing steps for content extraction and cleanup
5. Verify source language and detect content sections
6. Handle translation in manageable chunks to respect token limits
7. Apply post-processing to ensure formatting consistency
8. Perform quality checks on translated content
9. Account for idioms, technical terminology, and cultural nuances
10. Build and utilize a translation memory for consistency
11. Enable batch processing of multiple ePub files
12. Include contextual hints from surrounding paragraphs
13. Validate ePub structure integrity after translation
14. Support fallback mechanisms for failed translations
15. Generate detailed translation reports and quality metrics

Incorporate error handling, logging, and progress tracking. Ensure the system can gracefully recover from API failures or token limit issues.