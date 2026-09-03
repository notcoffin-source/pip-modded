Shader "Hidden/PiPDisabler/OutsideScopeKawaseBlur"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Offset ("Offset", Float) = 1
        _Opacity ("Opacity", Range(0,1)) = 0.45
        _Darkening ("Darkening", Range(0,1)) = 0.15
        _FlipY ("Flip Y", Float) = 0
        _RadialGateEnabled ("Radial Gate Enabled", Float) = 0
        _LensCenter ("Lens Center", Vector) = (0,0,0,0)
        _LensSize ("Lens Size", Vector) = (1,1,0,0)
        _ViewportAspect ("Viewport Aspect", Float) = 1
        _RadialGateStart ("Radial Gate Start", Float) = 1
        _RadialGateSoftness ("Radial Gate Softness", Float) = 0.25
        _TexelSize ("Texel Size", Vector) = (0.001,0.001,1,1)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "DualKawaseDown"

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _TexelSize;
            float _Offset;

            float2 FixUv(float2 uv)
            {
            #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0)
                    uv.y = 1.0 - uv.y;
            #endif
                return uv;
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = FixUv(i.uv);
                float2 o = _TexelSize.xy * _Offset;
                fixed4 c = tex2D(_MainTex, uv + float2( o.x,  o.y));
                c += tex2D(_MainTex, uv + float2(-o.x,  o.y));
                c += tex2D(_MainTex, uv + float2( o.x, -o.y));
                c += tex2D(_MainTex, uv + float2(-o.x, -o.y));
                return c * 0.25;
            }
            ENDCG
        }

        Pass
        {
            Name "DualKawaseUp"

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _TexelSize;
            float _Offset;

            float2 FixUv(float2 uv)
            {
            #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0)
                    uv.y = 1.0 - uv.y;
            #endif
                return uv;
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = FixUv(i.uv);
                float2 o = _TexelSize.xy * _Offset;

                fixed4 c = tex2D(_MainTex, uv) * 4.0;
                c += tex2D(_MainTex, uv + float2( o.x, 0.0)) * 2.0;
                c += tex2D(_MainTex, uv + float2(-o.x, 0.0)) * 2.0;
                c += tex2D(_MainTex, uv + float2(0.0,  o.y)) * 2.0;
                c += tex2D(_MainTex, uv + float2(0.0, -o.y)) * 2.0;
                c += tex2D(_MainTex, uv + float2( o.x,  o.y));
                c += tex2D(_MainTex, uv + float2(-o.x,  o.y));
                c += tex2D(_MainTex, uv + float2( o.x, -o.y));
                c += tex2D(_MainTex, uv + float2(-o.x, -o.y));
                return c * 0.0625;
            }
            ENDCG
        }

        Pass
        {
            Name "CompositeBackground"

            Stencil
            {
                Ref 0
                Comp Equal
                Pass Keep
                ReadMask 255
                WriteMask 0
            }

            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _BlurTex;
            float _Opacity;
            float _Darkening;
            float _FlipY;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5)
                    uv.y = 1.0 - uv.y;

                fixed4 c = tex2D(_BlurTex, uv);
                c.rgb *= 1.0 - saturate(_Darkening);
                c.a = saturate(_Opacity);
                return c;
            }
            ENDCG
        }

        Pass
        {
            Name "CompositeScopeBody"

            Stencil
            {
                Ref 2
                Comp Equal
                Pass Keep
                ReadMask 255
                WriteMask 0
            }

            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _BlurTex;
            float _Opacity;
            float _Darkening;
            float _FlipY;
            float _RadialGateEnabled;
            float4 _LensCenter;
            float4 _LensSize;
            float _ViewportAspect;
            float _RadialGateStart;
            float _RadialGateSoftness;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5)
                    uv.y = 1.0 - uv.y;

                fixed4 c = tex2D(_BlurTex, uv);
                c.rgb *= 1.0 - saturate(_Darkening);
                c.a = saturate(_Opacity);

                if (_RadialGateEnabled > 0.5)
                {
                    float2 clip = i.uv * 2.0 - 1.0;
                    float2 delta = clip - _LensCenter.xy;
                    delta.x *= _ViewportAspect;

                    float2 lensSize = _LensSize.xy;
                    lensSize.x *= _ViewportAspect;
                    float lensRadius = max(max(lensSize.x, lensSize.y) * 0.5, 0.001);
                    float radial = length(delta) / lensRadius;
                    float gate = smoothstep(_RadialGateStart, _RadialGateStart + _RadialGateSoftness, radial);
                    c.a *= gate;
                }

                return c;
            }
            ENDCG
        }

        Pass
        {
            Name "CompositeLens"

            Stencil
            {
                Ref 1
                Comp Equal
                Pass Keep
                ReadMask 255
                WriteMask 0
            }

            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _BlurTex;
            float _Opacity;
            float _Darkening;
            float _FlipY;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5)
                    uv.y = 1.0 - uv.y;

                fixed4 c = tex2D(_BlurTex, uv);
                c.rgb *= 1.0 - saturate(_Darkening);
                c.a = saturate(_Opacity);
                return c;
            }
            ENDCG
        }
    }
}
