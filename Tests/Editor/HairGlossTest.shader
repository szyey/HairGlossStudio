Shader "Hidden/HairGlossStudio/Tests"
{
    Properties
    {
        _UseMatCap ("Use MatCap", Float) = 0
        _MatCapTex ("MatCap", 2D) = "white" {}
        _UseMatCap2nd ("Use MatCap 2nd", Float) = 0
        _MatCap2ndTex ("MatCap 2nd", 2D) = "white" {}
        _AnisotropyMatCapStrength ("Anisotropy MatCap", Float) = 0
        _Anisotropy2ndMatCapStrength ("Anisotropy MatCap 2nd", Float) = 0
        _UseAnisotropy ("Use Anisotropy", Float) = 0
        _AnisotropyTangentMap ("Tangent", 2D) = "bump" {}
    }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 position : POSITION) : SV_POSITION { return position; }
            float4 frag() : SV_Target { return float4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
}
