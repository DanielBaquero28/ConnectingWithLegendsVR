using UnityEngine;
using CrazyMinnow.SALSA;


namespace DemoCode
{
		/// <summary>
		/// RELEASE NOTES:
		/// 	2.5.0:
		///			Initial release.
		/// ==========================================================================
		/// PURPOSE: This script provides access to audio data that is not available
		///		via the normal AudioClip API. This is particularly useful in
		///		instances where audio data is inserted into the filter chain via
		///		the OnAudioFilterRead() callback. For example, many TTS providers
		///		use this method to add audio data to the AudioSource.
		///
		/// CAVEATS:
		///		1. This must be placed on the GameObject with the AudioSource.
		///		2. SALSA must be configured to use external analysis.
		///		3. Accessing data in this manner is subject to other audio filter
		///			processing and may affect the dynamics of the analyzed audio.
		///			i.e. 3D positional audio will affect the data in this chain
		///				and will diminish audio dynamics, affecting lipsync.
		///  
		///		For the latest information visit crazyminnowstudio.com.
		/// ==========================================================================
		/// DISCLAIMER: While every attempt has been made to ensure the safe content
		///		and operation of these files, they are provided as-is, without
		///		warranty or guarantee of any kind. By downloading and using these
		///		files you are accepting any and all risks associated and release
		///		Crazy Minnow Studio, LLC of any and all liability.
		/// ==========================================================================
		/// </summary>
    public class SALSA_Template_AudioFilterProcessor : MonoBehaviour
    {
        public Salsa salsaInstance;
        private float[] analysisBuffer = new float[1024];
        private int bufferPointer = 0;
        private int interleave = 1;

        private void Awake()
        {
            if (!salsaInstance)
                salsaInstance = GetComponent<Salsa>();
            if (salsaInstance)
                salsaInstance.getExternalAnalysis = GetAnalysisValueLeveragingSalsaAnalyzer;
            else
                Debug.Log("SALSA not found...");
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            // Simply fill our buffer and keep it updated for ad-hoc analysis processing.

            // Only fill 'analysisBuffer' with channel 1 data. If you want
            // to store and keep track of additional channels, uncomment and
            // set 'interleave' to the number of channels of data passed into
            // this callback. Additionally adjust the for-loop as necessary.

            //interleave = channels;
            for (int i = 0; i < data.Length; i+=channels)
            {
                analysisBuffer[bufferPointer] = data[i];
                bufferPointer++;
                bufferPointer %= analysisBuffer.Length; // wrap the pointer if necessary
            }
        }

        // Utilize the built-in SALSA analyzer on your custom data.
        float GetAnalysisValueLeveragingSalsaAnalyzer()
        {
            // If you need more control over the analysis, process the buffer
            // here and then return the analysis. Since only the first channel of 
            // audio data is stored in the 'analysisBuffer' (in this example), the 
            // 'interleave' value is initialized as '1' -- we've already 
            // separated the data in the callback, so we want to analyze all of it.
            return salsaInstance.audioAnalyzer(interleave, analysisBuffer);
        }
    }
}