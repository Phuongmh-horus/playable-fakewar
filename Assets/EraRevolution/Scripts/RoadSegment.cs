using System.Collections.Generic;
using UnityEngine;
using GamePlay.Entities;

using GamePlay.Items;


#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GamePlay.Roads
{
    public class RoadSegment : PoolEntity
    {
        [Header("References")]
        [SerializeField] private MeshRenderer meshRenderer;

        [SerializeField] private Transform content;
        [SerializeField] private Transform finish;
        [SerializeField] private Transform totalSegment;
        [SerializeField] private Transform middlePoint;

        [Header("Validates")]
        [SerializeField] private bool autoUpdateOnValidate = true;

        [Header("Connections")]
        [SerializeField] private Transform entryPoint;
        [SerializeField] private Transform exitPoint;

        [Header("Segment Dimensions")]
        [SerializeField] private float contentLength = 300f;
        [SerializeField] private float finishLength = 50f;

        public Transform EntryPoint => entryPoint;
        public Transform ExitPoint => exitPoint;
        public Transform MiddlePoint => middlePoint;

        public float Length => (entryPoint != null && exitPoint != null)
            ? Vector3.Distance(entryPoint.position, exitPoint.position)
            : (contentLength + finishLength);

        public float TotalLength => contentLength + finishLength;
        public float ContentLength => contentLength;
        public float FinishLength => finishLength;

        public void SetLength(float newContentLength, float newFinishLength)
        {
            contentLength = newContentLength;
            finishLength = newFinishLength;
            UpdateSegmentDimensions();
        }

        private bool UpdateSegmentDimensions()
        {

            bool changed = false;

            // content scale Z
            var contentScale = content.localScale;
            if (!Mathf.Approximately(contentScale.z, contentLength))
            {
                contentScale.z = contentLength;
                content.localScale = contentScale;
                changed = true;
            }

            // finish position Z
            var finishPos = finish.localPosition;
            if (!Mathf.Approximately(finishPos.z, contentLength))
            {
                finishPos.z = contentLength;
                finish.localPosition = finishPos;
                changed = true;
            }

            // finish scale Z
            var finishScale = finish.localScale;
            if (!Mathf.Approximately(finishScale.z, finishLength))
            {
                finishScale.z = finishLength;
                finish.localScale = finishScale;
                changed = true;
            }

            // total segment scale Z
            if (totalSegment != null)
            {
                var totalScale = totalSegment.localScale;
                float totalLen = contentLength + finishLength;
                if (!Mathf.Approximately(totalScale.z, totalLen))
                {
                    totalScale.z = totalLen;
                    totalSegment.localScale = totalScale;
                    changed = true;
                }
            }

            // middle point pos Z
            if (middlePoint != null)
            {
                var middlePos = middlePoint.localPosition;
                if (!Mathf.Approximately(middlePos.z, contentLength))
                {
                    middlePos.z = contentLength;
                    middlePoint.localPosition = middlePos;
                    changed = true;
                }
            }

            // update entry/exit
            changed |= UpdateConnectionPoints();
            return changed;
        }

        private bool UpdateConnectionPoints()
        {
            if (entryPoint == null || exitPoint == null) return false;

            bool changed = false;

            if (entryPoint.localPosition != Vector3.zero)
            {
                entryPoint.localPosition = Vector3.zero;
                changed = true;
            }

            float totalLen = contentLength + finishLength;
            var targetExit = new Vector3(0f, 0f, totalLen);
            if (exitPoint.localPosition != targetExit)
            {
                exitPoint.localPosition = targetExit;
                changed = true;
            }

            return changed;
        }
    }
}
